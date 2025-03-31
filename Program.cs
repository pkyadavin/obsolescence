using Newtonsoft.Json.Linq;
using System.Xml.Linq;
using System.IO.Compression;
class Program
{
    static async Task Main(string[] args)
    {
        string token = "xxxxxxxxxxxxxxxxxxxxxxxxxxx"; // Load token from environment variable
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("GitHub token is missing.");
            return;
        }

        string url = "https://api.github.com/user/repos?per_page=1000"; // GitHub API URL to get repositories

        using (HttpClient client = new HttpClient())
        {
            // Add necessary headers for authentication and GitHub API request
            client.DefaultRequestHeaders.Add("User-Agent", "CSharpApp");
            client.DefaultRequestHeaders.Add("Authorization", $"token {token}");

            try
            {
                // Make the GET request to the GitHub API
                HttpResponseMessage response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode(); // Ensure a successful status code

                // Read the response content as string
                string responseContent = await response.Content.ReadAsStringAsync();

                // Parse the JSON response
                JArray repositories = JArray.Parse(responseContent);

                if (repositories.Count == 0)
                {
                    Console.WriteLine("No repositories found.");
                }
                else
                {
                    // Loop through the repositories
                    foreach (var repo in repositories)
                    {
                        string repoName = repo["name"].ToString();
                        string fullName = repo["full_name"].ToString();

                        // Check if the repository contains any file with "cs" in its name
                        bool containsCsFile = await CheckForCsFileRecursively(client, repo["owner"]["login"].ToString(), repoName, "");

                        if (containsCsFile)
                        {
                            // Display the repository name only if it contains a file with "cs" in the name
                            Console.WriteLine($"Repository: {fullName}");

                            // List dependencies if `.csproj` is found
                            await ListDependenciesInCsproj(client, repo["owner"]["login"].ToString(), repoName, "");
                        }
                    }
                }
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine($"HTTP Error: {e.Message}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"An error occurred: {e.Message}");
            }
        }
    }

    // Method to check if the repository contains a file with "cs" in its name recursively
    static async Task<bool> CheckForCsFileRecursively(HttpClient client, string owner, string repoName, string path)
    {
        string contentsUrl = $"https://api.github.com/repos/{owner}/{repoName}/contents/{path}";

        try
        {
            // Fetch the contents of the current folder
            HttpResponseMessage response = await client.GetAsync(contentsUrl);
            response.EnsureSuccessStatusCode(); // Ensure a successful status code

            // Read the response content as string
            string responseContent = await response.Content.ReadAsStringAsync();

            // Parse the JSON response (which lists the files and subfolders in the directory)
            JArray files = JArray.Parse(responseContent);

            // Loop through each file/folder in the current directory
            foreach (var file in files)
            {
                string fileName = file["name"].ToString();
                string fileType = file["type"].ToString();

                // Check if the file name contains "cs"
                if (fileType == "file" && fileName.Contains("csproj", StringComparison.OrdinalIgnoreCase))
                {
                    return true; // Return true if a file with "cs" in its name is found
                }
                else if (fileType == "dir")
                {
                    // Recurse into subdirectories
                    bool foundInSubdirectory = await CheckForCsFileRecursively(client, owner, repoName, $"{path}/{fileName}");
                    if (foundInSubdirectory)
                    {
                        return true; // Found a file with "cs" in its name in a subdirectory
                    }
                }
            }

            return false; // No file with "cs" in its name found in the current directory or subdirectories
        }
        catch (HttpRequestException)
        {
            // If the request fails (e.g., 404 or other errors), we consider that no file with "cs" is found
            return false;
        }
    }

    // Method to list dependencies in `.csproj` files
    static async Task ListDependenciesInCsproj(HttpClient client, string owner, string repoName, string path)
    {
        string contentsUrl = $"https://api.github.com/repos/{owner}/{repoName}/contents/{path}";

        try
        {
            // Fetch the contents of the current folder
            HttpResponseMessage response = await client.GetAsync(contentsUrl);
            response.EnsureSuccessStatusCode(); // Ensure a successful status code

            // Read the response content as string
            string responseContent = await response.Content.ReadAsStringAsync();

            // Parse the JSON response (which lists the files and subfolders in the directory)
            JArray files = JArray.Parse(responseContent);

            // Loop through each file/folder in the current directory
            foreach (var file in files)
            {
                string fileName = file["name"].ToString();
                string fileType = file["type"].ToString();

                // Check if the file is a `.csproj` file
                if (fileType == "file" && fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  Found .csproj: {fileName}");

                    // Download and parse the .csproj file
                    HttpResponseMessage csprojResponse = await client.GetAsync(file["download_url"].ToString());
                    csprojResponse.EnsureSuccessStatusCode();
                    string csprojContent = await csprojResponse.Content.ReadAsStringAsync();

                    // Parse the .csproj file to extract dependencies
                    XDocument csprojDoc = XDocument.Parse(csprojContent);
                    var packageReferences = csprojDoc.Descendants()
                        .Where(d => d.Name.LocalName == "PackageReference")
                        .Select(d => new
                        {
                            PackageName = d.Attribute("Include")?.Value,
                            Version = d.Attribute("Version")?.Value
                        });

                    // List the dependencies
                    if (packageReferences.Any())
                    {
                        Console.WriteLine($"  Dependencies in {fileName}:");
                        foreach (var package in packageReferences)
                        {
                            Console.WriteLine($"    - {package.PackageName}, Version: {package.Version}");

                          //  Check if the package is the latest version
                            string latestVersion = await GetLatestPackageVersion(package.PackageName);
                            if (latestVersion != null && !package.Version.Equals(latestVersion))
                            {
                                Console.WriteLine($"    (Outdated) - Latest Version: {latestVersion}, Current Version: {package.Version}");
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("  No dependencies found in this .csproj.");
                    }
                }
                else if (fileType == "dir")
                {
                    // Recurse into subdirectories
                    await ListDependenciesInCsproj(client, owner, repoName, $"{path}/{fileName}");
                }
            }
        }
        catch (HttpRequestException)
        {
            // Handle the error gracefully if the folder content can't be fetched
            Console.WriteLine($"  Failed to fetch content from {path}");
        }
    }

    // Method to get the latest version of a NuGet package
    static async Task<string> GetLatestPackageVersion(string packageName)
    {
        string nugetUrl = $"https://api.nuget.org/v3/registration5-gz-semver2/{packageName.ToLower()}/index.json";

        using (HttpClient client = new HttpClient())
        {
            try
            {
             string jsonResponse = await GetJsonResponseAsync(nugetUrl);
        
                // Parse the JSON response
                JObject json = JObject.Parse(jsonResponse);

                // Get all version ranges (lower and upper values)
                var items = json["items"].ToArray();

                // Find the latest version based on the upper version in the ranges
                var latestVersion = items
                    .Select(item => new
                    {
                        LowerVersion = (string)item["lower"],
                        UpperVersion = (string)item["upper"]
                    })
                    .Select(version => version.UpperVersion)
                    .OrderByDescending(version => new Version(version.Split('-')[0]))  // Sorting by major.minor.patch version
                    .FirstOrDefault();

                return latestVersion;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                // Return null if the package or version is not found or if there is an error
                return null;
            }
        }
    }

    // Async method to get the JSON response from NuGet API
    static async Task<string> GetJsonResponseAsync(string url)
    {
           using (HttpClient client = new HttpClient())
            {
                HttpResponseMessage response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                // Check for compression
                var contentEncoding = response.Content.Headers.ContentEncoding;
                Stream responseStream = await response.Content.ReadAsStreamAsync();

                if (contentEncoding.Contains("gzip"))
                {
                    // Decompress GZIP
                    using (var decompressedStream = new GZipStream(responseStream, CompressionMode.Decompress))
                    {
                        using (var reader = new StreamReader(decompressedStream))
                        {
                            string decompressedContent = await reader.ReadToEndAsync();
                            return decompressedContent;
                        }
                    }
                }
                else if (contentEncoding.Contains("deflate"))
                {
                    // Decompress Deflate
                    using (var decompressedStream = new DeflateStream(responseStream, CompressionMode.Decompress))
                    {
                        using (var reader = new StreamReader(decompressedStream))
                        {
                            string decompressedContent = await reader.ReadToEndAsync();
                            return decompressedContent;
                        }
                    }
                }
                else
                {
                    // No compression, read normally
                    return await response.Content.ReadAsStringAsync();
                }
            }

    }

}


