﻿namespace StorageAdapters.Azure
{
    using Platform;
    using Streams;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Xml.Linq;

    public sealed class AzureStorageService : HTTPStorageServiceBase<AzureConfiguration>
    {
        private static string[] PlatformFactoryTypes = {
            "StorageAdapters.Azure.WindowsStore.PlatformFactory, StorageAdapters.Azure.WindowsStore",
            "StorageAdapters.Azure.Desktop.PlatformFactory, StorageAdapters.Azure.Desktop"
        };

        private readonly IPlatformFactory platformFactory;
        public IPlatformFactory PlatformFactory
        {
            get
            {
                return platformFactory;
            }
        }

        private readonly ICryptographic cryptographic;

        public AzureStorageService() : base()
        {
            foreach (var platformFactoryType in PlatformFactoryTypes)
            {
                Type factoryType = Type.GetType(platformFactoryType);
                if (factoryType != null)
                {
                    platformFactory = (IPlatformFactory)Activator.CreateInstance(factoryType);
                }
            }

            if (platformFactory == null)
            {
                throw new PlatformNotSupportedException("No platform factory found, the following factories are supported:" + Environment.NewLine + string.Join(Environment.NewLine, PlatformFactoryTypes));
            }

            cryptographic = platformFactory.CreateCryptograhicModule();
        }

        #region HTTPStorageServiceBase

        public override async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            if (Configuration == null)
                throw new InvalidOperationException(Exceptions.ConfigurationMustBeSet);

            if (path == null)
                throw new ArgumentNullException(nameof(path));

            string[] pathParts = path.Split(Configuration.DirectorySeperator);

            // Create container if it does not exist
            if (!await ContainerExistAsync(pathParts[0], cancellationToken).ConfigureAwait(false))
            {
                await CreateContainerAsync(pathParts[0], cancellationToken).ConfigureAwait(false);
            }
        }

        public override async Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            if (Configuration == null)
                throw new InvalidOperationException(Exceptions.ConfigurationMustBeSet);

            if (path == null)
                throw new ArgumentNullException(nameof(path));

            string[] pathParts = PathUtility.Clean(Configuration.DirectorySeperator, path).Split(Configuration.DirectorySeperator);
            string containerName = pathParts[0];
            string directoryPath = string.Join(Configuration.DirectorySeperator.ToString(), pathParts.Skip(1)) + "/";

            if (pathParts.Length == 1)
            {
                await DeleteContainerAsync(containerName, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Delete all blobls with matching prefix
                List<string> files = new List<string>();
                string nextMarker = string.Empty;
                do
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, $"{containerName}?restype=container&comp=list&marker={Uri.EscapeDataString(nextMarker)}&prefix={Uri.EscapeDataString(directoryPath)}");
                    var response = await SendRequest(request, cancellationToken);
                    var responseDocument = System.Xml.Linq.XDocument.Parse(await response.Content.ReadAsStringAsync());

                    files.AddRange(from blob in responseDocument.Root.Element("Blobs").Elements("Blob")
                                   let properties = blob.Element("Properties")
                                   select blob.Element("Name").Value);

                    // Get the next marker from the current response
                    nextMarker = responseDocument.Root.Element("NextMarker").Value;
                }
                while (!string.IsNullOrEmpty(nextMarker));

                if (!files.Any())
                    throw new NotFoundException(string.Format(Exceptions.DirectoryNotFound, path));

                await Task.WhenAll(files.Select(fileName => DeleteFileAsync(PathUtility.Combine(Configuration.DirectorySeperator, containerName, fileName), cancellationToken)));
            }
        }

        public override async Task DeleteFileAsync(string path, CancellationToken cancellationToken)
        {
            if (Configuration == null)
                throw new InvalidOperationException(Exceptions.ConfigurationMustBeSet);

            if (path == null)
                throw new ArgumentNullException(nameof(path));

            await SendRequest(new HttpRequestMessage(HttpMethod.Delete, EncodePath(path)), cancellationToken);
        }

        public override async Task<bool> DirectoryExistAsync(string path, CancellationToken cancellationToken)
        {
            if (Configuration == null)
                throw new InvalidOperationException(Exceptions.ConfigurationMustBeSet);

            if (path == null)
                throw new ArgumentNullException(nameof(path));

            string[] pathParts = path.Split(Configuration.DirectorySeperator);

            // The root always exists
            if (pathParts.Length == 0)
            {
                return true;
            }
            else
            {
                // Only check if the container exists, all other directories always exists
                try
                {
                    await GetContainerProperties(pathParts[0], cancellationToken);
                    return true;
                }
                catch (NotFoundException)
                {
                    return false;
                }
            }

        }

        public override async Task<bool> FileExistAsync(string path, CancellationToken cancellationToken)
        {
            if (Configuration == null)
                throw new InvalidOperationException(Exceptions.ConfigurationMustBeSet);

            if (path == null)
                throw new ArgumentNullException(nameof(path));

            try
            {
                await GetFileAsync(path, cancellationToken);
                return true;
            }
            catch (NotFoundException)
            {
                return false;
            }
        }

        public override async Task<IEnumerable<IVirtualDirectory>> GetDirectoriesAsync(string path, CancellationToken cancellationToken)
        {
            if (Configuration == null)
                throw new InvalidOperationException(Exceptions.ConfigurationMustBeSet);

            if (path == null)
                throw new ArgumentNullException(nameof(path));

            if (PathUtility.IsRootPath(path))
            {
                return (await GetContainersAsync(cancellationToken).ConfigureAwait(false)).Select(containerName => new AzureDirectory()
                {
                    Name = containerName,
                    Path = containerName
                });
            }
            else
            {
                string[] pathParts = PathUtility.Clean(Configuration.DirectorySeperator, path).Split(Configuration.DirectorySeperator);
                string containerName = pathParts[0];
                string directoryPath = string.Join(Configuration.DirectorySeperator.ToString(), pathParts.Skip(1)) + "/";

                List<AzureDirectory> directories = new List<AzureDirectory>();
                string nextMarker = string.Empty;
                do
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, $"{containerName}?restype=container&comp=list&marker={Uri.EscapeDataString(nextMarker)}&prefix={Uri.EscapeDataString(directoryPath)}&delimiter={Configuration.DirectorySeperator}");
                    var response = await SendRequest(request, cancellationToken);
                    var responseDocument = System.Xml.Linq.XDocument.Parse(await response.Content.ReadAsStringAsync());

                    directories.AddRange(from blob in responseDocument.Root.Element("Blobs").Elements("BlobPrefix")
                                         select new AzureDirectory()
                                         {
                                             Name = blob.Element("Name").Value,
                                             Path = Path.Combine(path, blob.Element("Name").Value)
                                         });

                    // Get the next marker from the current response
                    nextMarker = responseDocument.Root.Element("NextMarker").Value;
                }
                while (!string.IsNullOrEmpty(nextMarker));

                return directories;
            }
        }

        public override async Task<IVirtualFileInfo> GetFileAsync(string path, CancellationToken cancellationToken)
        {
            if (Configuration == null)
                throw new InvalidOperationException(Exceptions.ConfigurationMustBeSet);

            if (path == null)
                throw new ArgumentNullException(nameof(path));

            var request = new HttpRequestMessage(HttpMethod.Head, EncodePath(path));
            var response = await SendRequest(request, cancellationToken);

            return new AzureFileInfo()
            {
                Name = PathUtility.GetFileName(Configuration.DirectorySeperator, path),
                LastModified = response.Content.Headers.LastModified.Value,
                Path = PathUtility.Clean(Configuration.DirectorySeperator, path),
                Size = response.Content.Headers.ContentLength.GetValueOrDefault(),
                BlobType = response.Headers.GetValues("x-ms-blob-type").Single()
            };
        }

        public override async Task<IEnumerable<IVirtualFileInfo>> GetFilesAsync(string path, CancellationToken cancellationToken)
        {
            if (Configuration == null)
                throw new InvalidOperationException(Exceptions.ConfigurationMustBeSet);

            if (path == null)
                throw new ArgumentNullException(nameof(path));

            string[] pathParts = PathUtility.Clean(Configuration.DirectorySeperator, path).Split(Configuration.DirectorySeperator);
            string containerName = pathParts[0];
            string directoryPath = string.Join(Configuration.DirectorySeperator.ToString(), pathParts.Skip(1)) + "/";

            List<AzureFileInfo> files = new List<AzureFileInfo>();
            string nextMarker = string.Empty;
            do
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{containerName}?restype=container&comp=list&marker={Uri.EscapeDataString(nextMarker)}&prefix={Uri.EscapeDataString(directoryPath)}&delimiter={Configuration.DirectorySeperator}");
                var response = await SendRequest(request, cancellationToken);
                var responseDocument = System.Xml.Linq.XDocument.Parse(await response.Content.ReadAsStringAsync());

                files.AddRange(from blob in responseDocument.Root.Element("Blobs").Elements("Blob")
                               let properties = blob.Element("Properties")
                               select new AzureFileInfo()
                               {
                                   Path = blob.Element("Name").Value,
                                   Name = PathUtility.GetFileName(Configuration.DirectorySeperator, blob.Element("Name").Value),
                                   Size = long.Parse(properties.Element("Content-Length").Value),
                                   LastModified = DateTime.ParseExact(properties.Element("Last-Modified").Value, "R", System.Globalization.CultureInfo.InvariantCulture)
                               });

                // Get the next marker from the current response
                nextMarker = responseDocument.Root.Element("NextMarker").Value;
            }
            while (!string.IsNullOrEmpty(nextMarker));

            return files;
        }

        public override async Task<Stream> ReadFileAsync(string path, CancellationToken cancellationToken)
        {
            if (Configuration == null)
                throw new InvalidOperationException(Exceptions.ConfigurationMustBeSet);

            if (path == null)
                throw new ArgumentNullException(nameof(path));

            var response = await SendRequest(new HttpRequestMessage(HttpMethod.Get, EncodePath(path)), cancellationToken);
            return await response.Content.ReadAsStreamAsync();
        }

        public override async Task SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken)
        {
            if (Configuration == null)
                throw new InvalidOperationException(Exceptions.ConfigurationMustBeSet);

            if (path == null)
                throw new ArgumentNullException(nameof(path));

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            // If the file exists, delete it.
            if (await (FileExistAsync(path, cancellationToken)))
            {
                await DeleteFileAsync(path, cancellationToken);
            }
            else
            {
                // Create a new empty block blob
                var createRequest = new HttpRequestMessage(HttpMethod.Put, EncodePath(path));
                createRequest.Headers.Add("x-ms-blob-type", "BlockBlob");
                await SendRequest(createRequest, cancellationToken);
            }

            // Use the append method to upload parts
            int count;
            byte[] buffer = new byte[4000000];
            List<string> blockIds = new List<string>();
            while ((count = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                blockIds.Add(await PutBlock(path, buffer, 0, count, cancellationToken));
            }

            await PutBlockList(path, blockIds, cancellationToken);
        }

        public async override Task AppendFileAsync(string path, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (Configuration == null)
                throw new InvalidOperationException(Exceptions.ConfigurationMustBeSet);

            if (path == null)
                throw new ArgumentNullException(nameof(path));

            if (count > 4000000)
                throw new StorageAdapterException(); // TODO: Messages

            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            IEnumerable<string> existingBlockIds;
            try
            {
                // Get existing block list
                var blockListResponse = await SendRequest(new HttpRequestMessage(HttpMethod.Get, EncodePath(path) + "?comp=blocklist&blocklisttype=committed"), cancellationToken);
                var blockListDocument = System.Xml.Linq.XDocument.Parse(await blockListResponse.Content.ReadAsStringAsync());
                existingBlockIds = from block in blockListDocument.Root.Element("CommittedBlocks").Elements("Block")
                                       select block.Element("Name").Value;
            }
            catch (NotFoundException)
            {
                // If the file don't exist create a new one instead
                using (MemoryStream ms = new MemoryStream())
                {
                    ms.Write(buffer, offset, count);
                    ms.Seek(0, SeekOrigin.Begin);
                    await SaveFileAsync(path, ms, cancellationToken);
                    return;
                }
            }

            // Upload the block
            string blockId = await PutBlock(path, buffer, offset, count, cancellationToken);

            // Saves the new block list
            await PutBlockList(path, existingBlockIds.Concat(new string[] { blockId }), cancellationToken);
        }

        private async Task<string> PutBlock(string path, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (Configuration == null)
                throw new InvalidOperationException(Exceptions.ConfigurationMustBeSet);

            if (path == null)
                throw new ArgumentNullException(nameof(path));

            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            string blockId = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
            var request = new HttpRequestMessage(HttpMethod.Put, EncodePath(path) + $"?comp=block&blockId={Uri.EscapeDataString(blockId)}");
            request.Content = new ByteArrayContent(buffer, offset, count);
            await SendRequest(request, cancellationToken);

            return blockId;
        }

        private async Task PutBlockList(string path, IEnumerable<string> blockIds, CancellationToken cancellationToken)
        {
            if (Configuration == null)
                throw new InvalidOperationException(Exceptions.ConfigurationMustBeSet);

            if (path == null)
                throw new ArgumentNullException(nameof(path));

            var putBlockListRequest = new HttpRequestMessage(HttpMethod.Put, EncodePath(path) + "?comp=blocklist");
            putBlockListRequest.Content = new StringContent(new XDocument(new XElement("BlockList", blockIds.Select(x => new XElement("Latest", x)))).ToString());
            await SendRequest(putBlockListRequest, cancellationToken);
        }

        #endregion

        #region AzureStorageService

        public async Task CreateContainerAsync(string containerName, CancellationToken cancellationToken)
        {
            if (containerName == null)
                throw new ArgumentNullException(nameof(containerName));

            await CreateContainerAsync(containerName, Configuration.DefaultContainerAccess, cancellationToken).ConfigureAwait(false);
        }

        public async Task CreateContainerAsync(string containerName, AzureBlobPublicBlobAccess access, CancellationToken cancellationToken)
        {
            if (containerName == null)
                throw new ArgumentNullException(nameof(containerName));

            var request = new HttpRequestMessage(HttpMethod.Put, EncodePath(containerName) + "?restype=container");
            if (access != AzureBlobPublicBlobAccess.@private)
            {
                request.Headers.Add("x-ms-blob-public-access", access.ToString());
            }

            await SendRequest(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> ContainerExistAsync(string containerName, CancellationToken cancellationToken)
        {
            if (containerName == null)
                throw new ArgumentNullException(nameof(containerName));

            try
            {
                await GetContainerProperties(containerName, cancellationToken);
                return true;
            }
            catch (NotFoundException)
            {
                return false;
            }
        }

        public async Task<Dictionary<string, string>> GetContainerProperties(string containerName, CancellationToken cancellationToken)
        {
            if (containerName == null)
                throw new ArgumentNullException(nameof(containerName));

            var request = new HttpRequestMessage(HttpMethod.Head, EncodePath(containerName) + "?restype=container");
            var response = await SendRequest(request, cancellationToken);

            return response.Headers.Where(x => x.Key.StartsWith("x-ms-")).ToDictionary(x => x.Key, x => string.Join(", ", x.Value));
        }

        public async Task<List<string>> GetContainersAsync(CancellationToken cancellationToken)
        {
            List<string> containers = new List<string>();
            string nextMarker = string.Empty;
            do
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "?comp=list&marker=" + Uri.EscapeDataString(nextMarker));
                var response = await SendRequest(request, cancellationToken);
                var responseDocument = System.Xml.Linq.XDocument.Parse(await response.Content.ReadAsStringAsync());

                containers.AddRange(responseDocument.Elements("Container").Select(x => x.Element("Name").Value));

                // Get the next marker from the current response
                nextMarker = responseDocument.Element("NextMarker").Value;
            }
            while (!string.IsNullOrEmpty(nextMarker));

            return containers;
        }

        public async Task DeleteContainerAsync(string containerName, CancellationToken cancellationToken)
        {
            if (containerName == null)
                throw new ArgumentNullException(nameof(containerName));

            await SendRequest(new HttpRequestMessage(HttpMethod.Delete, EncodePath(containerName) + "?restype=container"), cancellationToken).ConfigureAwait(false);
        }

        #endregion

        protected override void ConfigureHttpClient(HttpClient client)
        {
            if (string.IsNullOrWhiteSpace(Configuration.AzureVersion))
                throw new StorageAdapterException("Invalid configuration, AzureVersion must be set");

            // Set general headers
            client.DefaultRequestHeaders.Add("x-ms-version", Configuration.AzureVersion);

            if (!string.IsNullOrWhiteSpace(Configuration.ClientRequestId))
                client.DefaultRequestHeaders.Add("x-ms-client-request-id", Configuration.ClientRequestId);

            base.ConfigureHttpClient(client);
        }

        private async Task<HttpResponseMessage> SendRequest(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Create correct URI
            if (!request.RequestUri.IsAbsoluteUri)
            {
                UriBuilder uriBuilder = new UriBuilder(CombineUrl(string.Format(Configuration.APIAddress, Configuration.AccountName), request.RequestUri.OriginalString));
                uriBuilder.Scheme = Configuration.UseHTTPS ? "https" : "http";
                request.RequestUri = uriBuilder.Uri;
            }

            // Date header is always required.
            request.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("r"));

            // Create that darm Azure signature!
            string stringToSign = request.Method.Method.ToUpper() + "\n" +
                                  request.Content?.Headers.ContentEncoding + "\n" +
                                  request.Content?.Headers.ContentLanguage + "\n" +
                                  (request.Content?.Headers.ContentLength > 0 ? request.Content?.Headers.ContentLength?.ToString() : "") + "\n" +
                                  request.Content?.Headers.ContentMD5 + "\n" +
                                  request.Content?.Headers.ContentType + "\n" +
                                  request.Headers.Date?.UtcDateTime.ToString("R") + "\n" +
                                  request.Headers.IfModifiedSince?.UtcDateTime.ToString("R") + "\n" +
                                  request.Headers.IfMatch + "\n" +
                                  request.Headers.IfNoneMatch + "\n" +
                                  request.Headers.IfUnmodifiedSince?.UtcDateTime.ToString("R") + "\n" +
                                  request.Headers.Range + "\n" +
                                  CanonicalizedHeaders(request) + "\n" +
                                  CanonicalizedResources(request);

            // Sign signature and add as header
            string signature = Convert.ToBase64String(cryptographic.HMACSHA256(Convert.FromBase64String(Configuration.AccountKey), Encoding.UTF8.GetBytes(stringToSign)));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("SharedKey", Configuration.AccountName.Trim() + ":" + signature);

            var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    throw new NotFoundException(response.ReasonPhrase);
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    throw new UnauthorizedException(response.ReasonPhrase);
                }
                else
                {
                    throw new StorageAdapterException(Exceptions.UnknownException + Environment.NewLine + response.StatusCode + ": " + response.ReasonPhrase);
                }
            }

            return response;
        }

        private string CanonicalizedHeaders(HttpRequestMessage request)
        {
            List<string> headers = HttpClient.DefaultRequestHeaders.Concat(request.Headers).Select(x => x.Key.ToLower()).Distinct().ToList();
            headers.RemoveAll(x => !x.StartsWith("x-ms-")); // Remove all none x-ms- headers
            headers.Sort(); // This is suppose to be lexicographically 

            return string.Join("\n", headers.Select(header =>
            {
                IEnumerable<string> values;
                if (!request.Headers.TryGetValues(header, out values))
                {
                    values = HttpClient.DefaultRequestHeaders.GetValues(header);
                }

                return header + ":" + values.Single().Replace('\n', ' ').Trim();
            }));
        }

        private string CanonicalizedResources(HttpRequestMessage request)
        {
            List<string> queryParameters = request.RequestUri.Query.TrimStart('?').Split(new char[] { '&' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Uri.UnescapeDataString(x)).ToList();
            queryParameters.Sort();

            string resourcePath = "/" + Configuration.AccountName.Trim() + request.RequestUri.AbsolutePath;

            if (queryParameters.Any())
            {
                var queryParameterDict = queryParameters.ToLookup(x => x.Split('=')[0].ToLower(), x => x.Substring(x.IndexOf("=") + 1));
                string query = string.Join("\n", queryParameterDict.Select(x => x.Key + ":" + string.Join(",", x)));

                resourcePath += "\n" + query;
            }

            return resourcePath;
        }
    }
}
