using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace omnigage_email_template
{
    public class Program
    {
        static void Main(string[] args)
        {
            // Set token retrieved from Account -> Developer -> API Tokens
            string tokenKey = "";
            string tokenSecret = "";

            // Retrieve from Account -> Settings -> General -> "Key" field
            string accountKey = "";

            // API host path, only change if using sandbox (e.g., https://api.omnigage.io/api/v1/)
            string host = "";

            // Optional - Proxy configuration (e.g., http://debugproxy.com:8080)
            string proxyHost = "";
            string proxyUser = "";
            string proxyPass = "";

            // Local path to a PNG, JPG, or JPEG
            // On Mac, for example: "/Users/Shared/sample.png"
            List<string> filePaths = new List<string> { };

            // Subject of the email template
            string subject = "Example with Images";

            // Body to use when creating the email template
            string body = "Hello {{first-name}},<br /><br /><strong>Supports HTML</strong>{{unsubscribe-link}}<br /><br /><br />";

            try
            {
                MainAsync(tokenKey, tokenSecret, accountKey, host, subject, body, filePaths, proxyHost, proxyUser, proxyPass).Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        /// <summary>
        /// Upload files, create an email template and then link to the uploaded files.
        /// </summary>
        /// <param name="tokenKey"></param>
        /// <param name="tokenSecret"></param>
        /// <param name="accountKey"></param>
        /// <param name="host"></param>
		/// <param name="subject"></param>
        /// <param name="body"></param>
        /// <param name="filePaths"></param>
		/// <param name="proxyHost"></param>
		/// <param name="proxyUser"></param>
		/// <param name="proxyPass"></param>
        static async Task MainAsync(string tokenKey, string tokenSecret, string accountKey, string host, string subject, string body, List<string> filePaths, string proxyHost, string proxyUser, string proxyPass)
        {
            WebProxy proxy = null;

            if (proxyHost != "")
			{
                proxy = new WebProxy
                {
                    Address = new Uri(proxyHost),
                    BypassProxyOnLocal = false,
                    UseDefaultCredentials = false,

                    Credentials = new NetworkCredential(
                        userName: proxyUser,
                        password: proxyPass)
                };
            }

            var httpClientHandler = new HttpClientHandler();

            if (proxy != null)
			{
                httpClientHandler.Proxy = proxy;
			}

            using (var client = new HttpClient(handler: httpClientHandler, disposeHandler: true))
            {
                // Build basic authorization
                string authorization = CreateAuthorization(tokenKey, tokenSecret);

                // Set request context for Omnigage API
                client.BaseAddress = new Uri(host);
                client.DefaultRequestHeaders.Add("Authorization", "Basic " + authorization);
                client.DefaultRequestHeaders.Add("X-Account-Key", accountKey);
                client.DefaultRequestHeaders.Add("User-Agent", "Omnigage Email Template Demo");

                // A list of upload ids
                List<string> uploadIds = new List<string> { };

                // Upload each file and set the upload id
                foreach (var filePath in filePaths)
                {
                    string uploadId = await Upload(filePath.ToString(), client);
                    uploadIds.Add(uploadId);
                }

                // Build `email-template` instance payload and make request
                string emailTemplateContent = CreateEmailTemplateSchema(subject, body, uploadIds);
                JObject emailTemplateResponse = await PostRequest(client, "email-templates", emailTemplateContent);

                // Extract email template id
                string emailTemplateId = (string)emailTemplateResponse.SelectToken("data.id");
                Console.WriteLine($"Email Template ID: {emailTemplateId}");

                // Retrieve the converted uploads -> file ids
                List<string> fileIds = emailTemplateResponse.SelectToken("data.relationships.files.data").Select(f => (string)f.SelectToken("id")).ToList();

                // Retrieve each file URL and append image tag to the body
                foreach (string fileId in fileIds)
                {
                    JObject fileResponse = await GetRequest(client, $"files/{fileId}");
                    string fileUrl = (string)fileResponse.SelectToken("data.attributes.url");
                    body += CreateHtmlImg(fileUrl);
                }

                // Write out updated body
                Console.WriteLine($"Email Template Body: {body}");

                // Update the email template body to include the uploaded media
                string emailTemplateUpdatedContent = CreateEmailTemplateSchema(subject, body, null);
                JObject emailTemplateUpdated = await PatchRequest(client, $"email-templates/{emailTemplateId}", emailTemplateUpdatedContent);

                // Write out updated timestamp
                string emailTemplateUpdatedAt = (string)emailTemplateResponse.SelectToken("data.attributes.updated-at");
                Console.WriteLine($"Email Template Updated: {emailTemplateUpdatedAt}");
            };
        }

        /// <summary>
        /// Create Omnigage upload instance for signing S3 request. Upload `filePath` to S3 and return
        /// `upload` id to be used on another instance.
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="client"></param>
        /// <returns>Upload ID</returns>
        static async Task<string> Upload(string filePath, HttpClient client)
        {
            // Check that the file exists
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File {filePath} not found.");
            }

            // Collect meta on the file
            string fileName = Path.GetFileName(filePath);
            long fileSize = new System.IO.FileInfo(filePath).Length;
            string mimeType = GetMimeType(fileName);

            // Ensure proper MIME type
            if (mimeType == null)
            {
                throw new System.InvalidOperationException("Only PNG or JPG files accepted.");
            }

            // Build `upload` instance payload and make request
            string uploadContent = CreateUploadSchema(fileName, mimeType, fileSize);
            JObject uploadResponse = await PostRequest(client, "uploads", uploadContent);

            // Extract upload ID and request URL
            string uploadId = (string)uploadResponse.SelectToken("data.id");
            string requestUrl = (string)uploadResponse.SelectToken("data.attributes.request-url");

            Console.WriteLine($"Upload ID: {uploadId}");

            using (var clientS3 = new HttpClient())
            {
                // Create multipart form including setting form data and file content
                MultipartFormDataContent form = await CreateMultipartForm(uploadResponse, filePath, fileName, mimeType);

                // Upload to S3
                await PostS3Request(clientS3, uploadResponse, form, requestUrl);

                return uploadId;
            };
        }

        /// <summary>
        /// Create a POST request to the Omnigage API and return an object for retrieving tokens
        /// </summary>
        /// <param name="client"></param>
        /// <param name="uri"></param>
        /// <param name="content"></param>
        /// <returns>JObject</returns>
        static async Task<JObject> PostRequest(HttpClient client, string uri, string content)
        {
            StringContent payload = new StringContent(content, Encoding.UTF8, "application/json");
            HttpResponseMessage request = await client.PostAsync(uri, payload);
            string response = await request.Content.ReadAsStringAsync();
            return JObject.Parse(response);
        }

        /// <summary>
        /// Create a PATCH request to the Omnigage API and return an object for retrieving tokens
        /// </summary>
        /// <param name="client"></param>
        /// <param name="uri"></param>
        /// <param name="content"></param>
        /// <returns>JObject</returns>
        static async Task<JObject> PatchRequest(HttpClient client, string uri, string content)
        {
            StringContent payload = new StringContent(content, Encoding.UTF8, "application/json");

            var method = new HttpMethod("PATCH");
            var request = new HttpRequestMessage(method, uri)
            {
                Content = payload
            };

            HttpResponseMessage response = await client.SendAsync(request);

            string responseContent = await request.Content.ReadAsStringAsync();
            return JObject.Parse(responseContent);
        }

        /// <summary>
        /// Create a GET request to the Omnigage API and return an object for retrieving tokens
        /// </summary>
        /// <param name="client"></param>
        /// <param name="uri"></param>
        /// <returns>JObject</returns>
        static async Task<JObject> GetRequest(HttpClient client, string uri)
        {
            HttpResponseMessage response = await client.GetAsync(uri);
            response.EnsureSuccessStatusCode();
            string responseBody = await response.Content.ReadAsStringAsync();
            return JObject.Parse(responseBody);
        }

        /// <summary>
        /// Make a POST request to S3 using presigned headers and multipart form
        /// </summary>
        /// <param name="client"></param>
        /// <param name="uploadInstance"></param>
        /// <param name="form"></param>
        /// <param name="url"></param>
        static async Task PostS3Request(HttpClient client, JObject uploadInstance, MultipartFormDataContent form, string url)
        {
            object[] requestHeaders = uploadInstance.SelectToken("data.attributes.request-headers").Select(s => (object)s).ToArray();

            // Set each of the `upload` instance headers
            foreach (JObject header in requestHeaders)
            {
                foreach (KeyValuePair<string, JToken> prop in header)
                {
                    client.DefaultRequestHeaders.Add(prop.Key, (string)prop.Value);
                }
            }

            // Make S3 request
            HttpResponseMessage responseS3 = await client.PostAsync(url, form);
            string responseContent = await responseS3.Content.ReadAsStringAsync();

            if ((int)responseS3.StatusCode == 204)
            {
                Console.WriteLine("Successfully uploaded file.");
            }
            else
            {
                Console.WriteLine(responseS3);
                throw new S3UploadFailed();
            }
        }

        /// <summary>
        /// Create a multipart form using form data from the Omnigage `upload` instance along with the specified file path.
        /// </summary>
        /// <param name="uploadInstance"></param>
        /// <param name="filePath"></param>
        /// <param name="fileName"></param>
        /// <param name="mimeType"></param>
        /// <returns>A multipart form</returns>
        static async Task<MultipartFormDataContent> CreateMultipartForm(JObject uploadInstance, string filePath, string fileName, string mimeType)
        {
            // Retrieve values to use for uploading to S3
            object[] requestFormData = uploadInstance.SelectToken("data.attributes.request-form-data").Select(s => (object)s).ToArray();

            MultipartFormDataContent form = new MultipartFormDataContent("Upload----" + DateTime.Now.ToString(CultureInfo.InvariantCulture));

            // Set each of the `upload` instance form data
            foreach (JObject formData in requestFormData)
            {
                foreach (KeyValuePair<string, JToken> prop in formData)
                {
                    form.Add(new StringContent((string)prop.Value), prop.Key);
                }
            }

            // Set the content type (required by presigned URL)
            form.Add(new StringContent(mimeType), "Content-Type");

            // Read file into result
            byte[] result;
            using (FileStream stream = File.Open(filePath, FileMode.Open))
            {
                result = new byte[stream.Length];
                await stream.ReadAsync(result, 0, (int)stream.Length);
            }

            // Add file content to form
            ByteArrayContent fileContent = new ByteArrayContent(result);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
            form.Add(fileContent, "file", fileName);

            return form;
        }

        /// <summary>
        /// Determine MIME type based on the file name.
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns>MIME type</returns>
        static string GetMimeType(string fileName)
        {
            string extension = Path.GetExtension(fileName);

            if (extension == ".png")
            {
                return "image/png";
            }
            else if (extension == ".jpg" || extension == ".jpeg")
            {
                return "image/jpeg";
            }

            return null;
        }

        /// <summary>
        /// Create Omnigage `uploads` schema
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="mimeType"></param>
        /// <param name="fileSize"></param>
        /// <returns>JSON</returns>
        static string CreateUploadSchema(string fileName, string mimeType, long fileSize)
        {
            return @"{
                'name': '" + fileName + @"',
                'type': '" + mimeType + @"',
                'size': " + fileSize + @"
            }";
        }

        /// <summary>
        /// Create Omnigage `email-templates` schema
        /// </summary>
        /// <param name="body"></param>
        /// <param name="uploadIds"></param>
        /// <returns>JSON</returns>
        static string CreateEmailTemplateSchema(string subject, string body, List<string> uploadIds = null)
        {
            if (uploadIds == null)
            {
                uploadIds = new List<string>();
            }

            string uploadInstances = "";
            foreach (var uploadId in uploadIds)
            {
                if (uploadInstances != "")
                {
                    uploadInstances += ",";
                }

                uploadInstances += @"{
                    ""type"": ""uploads"",
                    ""id"": """ + uploadId + @"""
                }";
            }

            return @"{
                ""data"":{
                    ""attributes"":{
                        ""subject"":""" + subject + @""",
                        ""body"":""" + EscapeForJson(body) + @"""
                    },
                    ""relationships"":{
                        ""upload-files"":{
                            ""data"": [" + uploadInstances + @"]
                        }
                    },
                    ""type"":""email-templates""
                }
            }";
        }

        /// <summary>
        /// Escape string JSON
        /// </summary>
        /// <param name="s"></param>
        /// <returns>JSON</returns>
        static string EscapeForJson(string s)
        {
            string quoted = JsonConvert.ToString(s);
            return quoted.Substring(1, quoted.Length - 2);
        }

        /// <summary>
        /// Create HTML img tag
        /// </summary>
        /// <param name="url"></param>
        /// <returns>HTML</returns>
        static string CreateHtmlImg(string url)
        {
            return @"<img src=""" + url + @""" />";
        }

        /// <summary>
        /// Create Authorization token following RFC 2617 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="secret"></param>
        /// <returns>Base64 encoded string</returns>
        static string CreateAuthorization(string key, string secret)
        {
            byte[] authBytes = System.Text.Encoding.UTF8.GetBytes($"{key}:{secret}");
            return System.Convert.ToBase64String(authBytes);
        }

        /// <summary>
        /// S3 Upload Failed exception
        /// </summary>
        public class S3UploadFailed : Exception { }
    }
}