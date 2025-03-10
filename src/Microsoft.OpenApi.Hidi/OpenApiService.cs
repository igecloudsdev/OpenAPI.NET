﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OData.Edm.Csdl;
using Microsoft.OpenApi.ApiManifest;
using Microsoft.OpenApi.ApiManifest.OpenAI;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Hidi.Formatters;
using Microsoft.OpenApi.Hidi.Options;
using Microsoft.OpenApi.Hidi.Utilities;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.OData;
using Microsoft.OpenApi.Readers;
using Microsoft.OpenApi.Services;
using Microsoft.OpenApi.Writers;
using static Microsoft.OpenApi.Hidi.OpenApiSpecVersionHelper;

namespace Microsoft.OpenApi.Hidi
{
    internal static class OpenApiService
    {
        /// <summary>
        /// Implementation of the transform command
        /// </summary>
        public static async Task TransformOpenApiDocument(HidiOptions options, ILogger logger, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(options.OpenApi) && string.IsNullOrEmpty(options.Csdl) && string.IsNullOrEmpty(options.FilterOptions?.FilterByApiManifest))
            {
                throw new ArgumentException("Please input a file path or URL");
            }

            try
            {
                if (options.Output == null)
                {
                    var inputExtension = GetInputPathExtension(options.OpenApi, options.Csdl);
                    options.Output = new FileInfo($"./output{inputExtension}");
                };

                if (options.CleanOutput && options.Output.Exists)
                {
                    options.Output.Delete();
                }
                if (options.Output.Exists)
                {
                    throw new IOException($"The file {options.Output} already exists. Please input a new file path.");
                }

                // Default to yaml and OpenApiVersion 3 during csdl to OpenApi conversion
                OpenApiFormat openApiFormat = options.OpenApiFormat ?? (!string.IsNullOrEmpty(options.OpenApi) ? GetOpenApiFormat(options.OpenApi, logger) : OpenApiFormat.Yaml);
                OpenApiSpecVersion openApiVersion = options.Version != null ? TryParseOpenApiSpecVersion(options.Version) : OpenApiSpecVersion.OpenApi3_0;

                // If ApiManifest is provided, set the referenced OpenAPI document
                var apiDependency = await FindApiDependency(options.FilterOptions?.FilterByApiManifest, logger, cancellationToken);
                if (apiDependency != null)
                {
                    options.OpenApi = apiDependency.ApiDescripionUrl;
                }

                // If Postman Collection is provided, load it
                JsonDocument postmanCollection = null;
                if (!String.IsNullOrEmpty(options.FilterOptions?.FilterByCollection))
                {
                    using (var collectionStream = await GetStream(options.FilterOptions.FilterByCollection, logger, cancellationToken))
                    {
                        postmanCollection = JsonDocument.Parse(collectionStream);
                    }
                }

                // Load OpenAPI document
                OpenApiDocument document = await GetOpenApi(options, logger, cancellationToken, options.MetadataVersion);

                if (options.FilterOptions != null)
                {
                    document = ApplyFilters(options, logger, apiDependency, postmanCollection, document);
                }

                var languageFormat = options.SettingsConfig?.GetSection("LanguageFormat")?.Value;
                if (Extensions.StringExtensions.IsEquals(languageFormat, "PowerShell"))
                {
                    // PowerShell Walker.
                    var powerShellFormatter = new PowerShellFormatter();
                    var walker = new OpenApiWalker(powerShellFormatter);
                    walker.Walk(document);
                }
                WriteOpenApi(options, openApiFormat, openApiVersion, document, logger);
            }
            catch (TaskCanceledException)
            {
                Console.Error.WriteLine("CTRL+C pressed, aborting the operation.");
            }
            catch (IOException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not transform the document, reason: {ex.Message}", ex);
            }
        }

        private static async Task<ApiDependency> FindApiDependency(string apiManifestPath, ILogger logger, CancellationToken cancellationToken)
        {
            ApiDependency apiDependency = null;
            // If API Manifest is provided, load it, use it get the OpenAPI path
            ApiManifestDocument apiManifest = null;
            if (!string.IsNullOrEmpty(apiManifestPath))
            {
                // Extract fragment identifier if passed as the name of the ApiDependency
                var apiManifestRef = apiManifestPath.Split('#');
                string apiDependencyName = null;
                if (apiManifestRef.Length > 1)
                {
                    apiDependencyName = apiManifestRef[1];
                }
                using (var fileStream = await GetStream(apiManifestRef[0], logger, cancellationToken))
                {
                    apiManifest = ApiManifestDocument.Load(JsonDocument.Parse(fileStream).RootElement);
                }

                apiDependency = apiDependencyName != null ? apiManifest.ApiDependencies[apiDependencyName] : apiManifest.ApiDependencies.First().Value;
            }

            return apiDependency;
        }

        private static OpenApiDocument ApplyFilters(HidiOptions options, ILogger logger, ApiDependency apiDependency, JsonDocument postmanCollection, OpenApiDocument document)
        {
            Dictionary<string, List<string>> requestUrls = null;
            if (apiDependency != null)
            {
                requestUrls = GetRequestUrlsFromManifest(apiDependency);
            }
            else if (postmanCollection != null)
            {
                requestUrls = EnumerateJsonDocument(postmanCollection.RootElement, requestUrls);
                logger.LogTrace("Finished fetching the list of paths and Http methods defined in the Postman collection.");
            }

            logger.LogTrace("Creating predicate from filter options.");
            var predicate = FilterOpenApiDocument(options.FilterOptions.FilterByOperationIds,
                                                    options.FilterOptions.FilterByTags,
                                                    requestUrls,
                                                    document,
                                                     logger);
            if (predicate != null)
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();
                document = OpenApiFilterService.CreateFilteredDocument(document, predicate);
                stopwatch.Stop();
                logger.LogTrace("{timestamp}ms: Creating filtered OpenApi document with {paths} paths.", stopwatch.ElapsedMilliseconds, document.Paths.Count);
            }

            return document;
        }

        private static void WriteOpenApi(HidiOptions options, OpenApiFormat openApiFormat, OpenApiSpecVersion openApiVersion, OpenApiDocument document, ILogger logger)
        {
            using (logger.BeginScope("Output"))
            {
                using var outputStream = options.Output.Create();
                var textWriter = new StreamWriter(outputStream);

                var settings = new OpenApiWriterSettings()
                {
                    InlineLocalReferences = options.InlineLocal,
                    InlineExternalReferences = options.InlineExternal
                };

                IOpenApiWriter writer = openApiFormat switch
                {
                    OpenApiFormat.Json => options.TerseOutput ? new OpenApiJsonWriter(textWriter, settings, options.TerseOutput) : new OpenApiJsonWriter(textWriter, settings, false),
                    OpenApiFormat.Yaml => new OpenApiYamlWriter(textWriter, settings),
                    _ => throw new ArgumentException("Unknown format"),
                };

                logger.LogTrace("Serializing to OpenApi document using the provided spec version and writer");

                var stopwatch = new Stopwatch();
                stopwatch.Start();
                document.Serialize(writer, openApiVersion);
                stopwatch.Stop();

                logger.LogTrace($"Finished serializing in {stopwatch.ElapsedMilliseconds}ms");
                textWriter.Flush();
            }
        }

        // Get OpenAPI document either from OpenAPI or CSDL 
        private static async Task<OpenApiDocument> GetOpenApi(HidiOptions options, ILogger logger, CancellationToken cancellationToken, string metadataVersion = null)
        {

            OpenApiDocument document;
            Stream stream;

            if (!string.IsNullOrEmpty(options.Csdl))
            {
                var stopwatch = new Stopwatch();
                using (logger.BeginScope("Convert CSDL: {csdl}", options.Csdl))
                {
                    stopwatch.Start();
                    stream = await GetStream(options.Csdl, logger, cancellationToken);
                    Stream filteredStream = null;
                    if (!string.IsNullOrEmpty(options.CsdlFilter))
                    {
                        XslCompiledTransform transform = GetFilterTransform();
                        filteredStream = ApplyFilterToCsdl(stream, options.CsdlFilter, transform);
                        filteredStream.Position = 0;
                        stream.Dispose();
                        stream = null;
                    }

                    document = await ConvertCsdlToOpenApi(filteredStream ?? stream, metadataVersion, options.SettingsConfig, cancellationToken);
                    stopwatch.Stop();
                    logger.LogTrace("{timestamp}ms: Generated OpenAPI with {paths} paths.", stopwatch.ElapsedMilliseconds, document.Paths.Count);
                }
            }
            else
            {
                stream = await GetStream(options.OpenApi, logger, cancellationToken);
                var result = await ParseOpenApi(options.OpenApi, options.InlineExternal, logger, stream, cancellationToken);
                document = result.OpenApiDocument;
            }

            return document;
        }

        private static Func<string, OperationType?, OpenApiOperation, bool> FilterOpenApiDocument(string filterbyoperationids, string filterbytags, Dictionary<string, List<string>> requestUrls, OpenApiDocument document, ILogger logger)
        {
            Func<string, OperationType?, OpenApiOperation, bool> predicate = null;

            using (logger.BeginScope("Create Filter"))
            {
                // Check if filter options are provided, then slice the OpenAPI document
                if (!string.IsNullOrEmpty(filterbyoperationids) && !string.IsNullOrEmpty(filterbytags))
                {
                    throw new InvalidOperationException("Cannot filter by operationIds and tags at the same time.");
                }
                if (!string.IsNullOrEmpty(filterbyoperationids))
                {
                    logger.LogTrace("Creating predicate based on the operationIds supplied.");
                    predicate = OpenApiFilterService.CreatePredicate(tags: filterbyoperationids);

                }
                if (!string.IsNullOrEmpty(filterbytags))
                {
                    logger.LogTrace("Creating predicate based on the tags supplied.");
                    predicate = OpenApiFilterService.CreatePredicate(tags: filterbytags);

                }
                if (requestUrls != null)
                {
                    logger.LogTrace("Creating predicate based on the paths and Http methods defined in the Postman collection.");
                    predicate = OpenApiFilterService.CreatePredicate(requestUrls: requestUrls, source: document);
                }
            }

            return predicate;
        }

        private static Dictionary<string, List<string>> GetRequestUrlsFromManifest(ApiDependency apiDependency)
        {
            // Get the request URLs from the API Dependencies in the API manifest 
            var requests = apiDependency
                    .Requests.Where(static r => !r.Exclude)
                                .Select(static r => new { UriTemplate = r.UriTemplate, Method = r.Method })
                    .GroupBy(static r => r.UriTemplate)
                    .ToDictionary(static g => g.Key, static g => g.Select(static r => r.Method).ToList());
            // This makes the assumption that the UriTemplate in the ApiManifest matches exactly the UriTemplate in the OpenAPI document
            // This does not need to be the case.  The URI template in the API manifest could map to a set of OpenAPI paths.
            // Additional logic will be required to handle this scenario.  I sugggest we build this into the OpenAPI.Net library at some point.
            return requests;
        }

        private static XslCompiledTransform GetFilterTransform()
        {
            XslCompiledTransform transform = new();
            Assembly assembly = typeof(OpenApiService).GetTypeInfo().Assembly;
            Stream xslt = assembly.GetManifestResourceStream("Microsoft.OpenApi.Hidi.CsdlFilter.xslt");
            transform.Load(new XmlTextReader(new StreamReader(xslt)));
            return transform;
        }

        private static Stream ApplyFilterToCsdl(Stream csdlStream, string entitySetOrSingleton, XslCompiledTransform transform)
        {
            Stream stream;
            using StreamReader inputReader = new(csdlStream, leaveOpen: true);
            XmlReader inputXmlReader = XmlReader.Create(inputReader);
            MemoryStream filteredStream = new();
            StreamWriter writer = new(filteredStream);
            XsltArgumentList args = new();
            args.AddParam("entitySetOrSingleton", "", entitySetOrSingleton);
            transform.Transform(inputXmlReader, args, writer);
            stream = filteredStream;
            return stream;
        }

        /// <summary>
        /// Implementation of the validate command
        /// </summary>
        public static async Task ValidateOpenApiDocument(
            string openapi,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(openapi))
            {
                throw new ArgumentNullException(nameof(openapi));
            }

            try
            {
                using var stream = await GetStream(openapi, logger, cancellationToken);

                var result = await ParseOpenApi(openapi, false, logger, stream, cancellationToken);

                using (logger.BeginScope("Calculating statistics"))
                {
                    var statsVisitor = new StatsVisitor();
                    var walker = new OpenApiWalker(statsVisitor);
                    walker.Walk(result.OpenApiDocument);

                    logger.LogTrace("Finished walking through the OpenApi document. Generating a statistics report..");
                    logger.LogInformation(statsVisitor.GetStatisticsReport());
                }
            }
            catch (TaskCanceledException)
            {
                Console.Error.WriteLine("CTRL+C pressed, aborting the operation.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not validate the document, reason: {ex.Message}", ex);
            }
        }

        private static async Task<ReadResult> ParseOpenApi(string openApiFile, bool inlineExternal, ILogger logger, Stream stream, CancellationToken cancellationToken)
        {
            ReadResult result;
            Stopwatch stopwatch = Stopwatch.StartNew();
            using (logger.BeginScope("Parsing OpenAPI: {openApiFile}", openApiFile))
            {
                stopwatch.Start();

                result = await new OpenApiStreamReader(new OpenApiReaderSettings
                {
                    LoadExternalRefs = inlineExternal,
                    BaseUrl = openApiFile.StartsWith("http", StringComparison.OrdinalIgnoreCase) ?
                        new Uri(openApiFile) :
                        new Uri("file://" + new FileInfo(openApiFile).DirectoryName + Path.DirectorySeparatorChar)
                }
                ).ReadAsync(stream, cancellationToken);

                logger.LogTrace("{timestamp}ms: Completed parsing.", stopwatch.ElapsedMilliseconds);

                LogErrors(logger, result);
                stopwatch.Stop();
            }

            return result;
        }

        /// <summary>
        /// Converts CSDL to OpenAPI
        /// </summary>
        /// <param name="csdl">The CSDL stream.</param>
        /// <returns>An OpenAPI document.</returns>
        public static async Task<OpenApiDocument> ConvertCsdlToOpenApi(Stream csdl, string metadataVersion = null, IConfiguration settings = null, CancellationToken token = default)
        {
            using var reader = new StreamReader(csdl);
            var csdlText = await reader.ReadToEndAsync(token);
            var edmModel = CsdlReader.Parse(XElement.Parse(csdlText).CreateReader());
            settings ??= SettingsUtilities.GetConfiguration();

            OpenApiDocument document = edmModel.ConvertToOpenApi(SettingsUtilities.GetOpenApiConvertSettings(settings, metadataVersion));
            document = FixReferences(document);

            return document;
        }

        /// <summary>
        /// Fixes the references in the resulting OpenApiDocument.
        /// </summary>
        /// <param name="document"> The converted OpenApiDocument.</param>
        /// <returns> A valid OpenApiDocument instance.</returns>
        public static OpenApiDocument FixReferences(OpenApiDocument document)
        {
            // This method is only needed because the output of ConvertToOpenApi isn't quite a valid OpenApiDocument instance.
            // So we write it out, and read it back in again to fix it up.

            var sb = new StringBuilder();
            document.SerializeAsV3(new OpenApiYamlWriter(new StringWriter(sb)));
            var doc = new OpenApiStringReader().Read(sb.ToString(), out _);

            return doc;
        }

        /// <summary>
        /// Takes in a file stream, parses the stream into a JsonDocument and gets a list of paths and Http methods
        /// </summary>
        /// <param name="stream"> A file stream.</param>
        /// <returns> A dictionary of request urls and http methods from a collection.</returns>
        public static Dictionary<string, List<string>> ParseJsonCollectionFile(Stream stream, ILogger logger)
        {
            var requestUrls = new Dictionary<string, List<string>>();

            logger.LogTrace("Parsing the json collection file into a JsonDocument");
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            requestUrls = EnumerateJsonDocument(root, requestUrls);
            logger.LogTrace("Finished fetching the list of paths and Http methods defined in the Postman collection.");

            return requestUrls;
        }

        private static Dictionary<string, List<string>> EnumerateJsonDocument(JsonElement itemElement, Dictionary<string, List<string>> paths)
        {
            var itemsArray = itemElement.GetProperty("item");

            foreach (var item in itemsArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    if (item.TryGetProperty("request", out var request))
                    {
                        // Fetch list of methods and urls from collection, store them in a dictionary
                        var path = request.GetProperty("url").GetProperty("raw").ToString();
                        var method = request.GetProperty("method").ToString();
                        if (!paths.ContainsKey(path))
                        {
                            paths.Add(path, new List<string> { method });
                        }
                        else
                        {
                            paths[path].Add(method);
                        }
                    }
                    else
                    {
                        EnumerateJsonDocument(item, paths);
                    }
                }
                else
                {
                    EnumerateJsonDocument(item, paths);
                }
            }

            return paths;
        }

        /// <summary>
        /// Reads stream from file system or makes HTTP request depending on the input string
        /// </summary>
        private static async Task<Stream> GetStream(string input, ILogger logger, CancellationToken cancellationToken)
        {
            Stream stream;
            using (logger.BeginScope("Reading input stream"))
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();

                if (input.StartsWith("http"))
                {
                    try
                    {
                        var httpClientHandler = new HttpClientHandler()
                        {
                            SslProtocols = System.Security.Authentication.SslProtocols.Tls12,
                        };
                        using var httpClient = new HttpClient(httpClientHandler)
                        {
                            DefaultRequestVersion = HttpVersion.Version20
                        };
                        stream = await httpClient.GetStreamAsync(input, cancellationToken);
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new InvalidOperationException($"Could not download the file at {input}", ex);
                    }
                }
                else
                {
                    try
                    {
                        var fileInput = new FileInfo(input);
                        stream = fileInput.OpenRead();
                    }
                    catch (Exception ex) when (ex is FileNotFoundException ||
                        ex is PathTooLongException ||
                        ex is DirectoryNotFoundException ||
                        ex is IOException ||
                        ex is UnauthorizedAccessException ||
                        ex is SecurityException ||
                        ex is NotSupportedException)
                    {
                        throw new InvalidOperationException($"Could not open the file at {input}", ex);
                    }
                }
                stopwatch.Stop();
                logger.LogTrace("{timestamp}ms: Read file {input}", stopwatch.ElapsedMilliseconds, input);
            }
            return stream;
        }

        /// <summary>
        /// Attempt to guess OpenAPI format based in input URL
        /// </summary>
        /// <param name="input"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        private static OpenApiFormat GetOpenApiFormat(string input, ILogger logger)
        {
            logger.LogTrace("Getting the OpenApi format");
            return !input.StartsWith("http") && Path.GetExtension(input) == ".json" ? OpenApiFormat.Json : OpenApiFormat.Yaml;
        }

        private static string GetInputPathExtension(string openapi = null, string csdl = null)
        {
            var extension = String.Empty;
            if (!string.IsNullOrEmpty(openapi))
            {
                extension = Path.GetExtension(openapi);
            }
            else if (!string.IsNullOrEmpty(csdl))
            {
                extension = ".yml";
            }

            return extension;
        }

        internal static async Task<string> ShowOpenApiDocument(HidiOptions options, ILogger logger, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrEmpty(options.OpenApi) && string.IsNullOrEmpty(options.Csdl))
                {
                    throw new ArgumentException("Please input a file path or URL");
                }

                var document = await GetOpenApi(options, logger, cancellationToken);

                using (logger.BeginScope("Creating diagram"))
                {
                    // If output is null, create a HTML file in the user's temporary directory
                    if (options.Output == null)
                    {
                        var tempPath = Path.GetTempPath() + "/hidi/";
                        if (!File.Exists(tempPath))
                        {
                            Directory.CreateDirectory(tempPath);
                        }

                        var fileName = Path.GetRandomFileName();

                        var output = new FileInfo(Path.Combine(tempPath, fileName + ".html"));
                        using (var file = new FileStream(output.FullName, FileMode.Create))
                        {
                            using var writer = new StreamWriter(file);
                            WriteTreeDocumentAsHtml(options.OpenApi ?? options.Csdl, document, writer);
                        }
                        logger.LogTrace("Created Html document with diagram ");

                        // Launch a browser to display the output html file
                        using var process = new Process();
                        process.StartInfo.FileName = output.FullName;
                        process.StartInfo.UseShellExecute = true;
                        process.Start();

                        return output.FullName;
                    }
                    else  // Write diagram as Markdown document to output file
                    {
                        using (var file = new FileStream(options.Output.FullName, FileMode.Create))
                        {
                            using var writer = new StreamWriter(file);
                            WriteTreeDocumentAsMarkdown(options.OpenApi ?? options.Csdl, document, writer);
                        }
                        logger.LogTrace("Created markdown document with diagram ");
                        return options.Output.FullName;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                Console.Error.WriteLine("CTRL+C pressed, aborting the operation.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not generate the document, reason: {ex.Message}", ex);
            }
            return null;
        }

        private static void LogErrors(ILogger logger, ReadResult result)
        {
            var context = result.OpenApiDiagnostic;
            if (context.Errors.Count != 0)
            {
                using (logger.BeginScope("Detected errors"))
                {
                    foreach (var error in context.Errors)
                    {
                        logger.LogError("Detected error during parsing: {error}", error.ToString());
                    }
                }
            }
        }

        internal static void WriteTreeDocumentAsMarkdown(string openapiUrl, OpenApiDocument document, StreamWriter writer)
        {
            var rootNode = OpenApiUrlTreeNode.Create(document, "main");

            writer.WriteLine("# " + document.Info.Title);
            writer.WriteLine();
            writer.WriteLine("API Description: " + openapiUrl);

            writer.WriteLine(@"<div>");
            // write a span for each mermaidcolorscheme
            foreach (var style in OpenApiUrlTreeNode.MermaidNodeStyles)
            {
                writer.WriteLine($"<span style=\"padding:2px;background-color:{style.Value.Color};border: 2px solid\">{style.Key.Replace("_", " ")}</span>");
            }
            writer.WriteLine("</div>");
            writer.WriteLine();
            writer.WriteLine("```mermaid");
            rootNode.WriteMermaid(writer);
            writer.WriteLine("```");
        }

        internal static void WriteTreeDocumentAsHtml(string sourceUrl, OpenApiDocument document, StreamWriter writer, bool asHtmlFile = false)
        {
            var rootNode = OpenApiUrlTreeNode.Create(document, "main");

            writer.WriteLine(@"<!doctype html>
<html>
<head>
  <meta charset=""utf-8""/>
  <script src=""https://cdnjs.cloudflare.com/ajax/libs/mermaid/8.0.0/mermaid.min.js""></script>
</head>
<style>
    body {
        font-family: Verdana, sans-serif;
    }
</style>
<body>");
            writer.WriteLine("<h1>" + document.Info.Title + "</h1>");
            writer.WriteLine();
            writer.WriteLine($"<h3> API Description: <a href='{sourceUrl}'>{sourceUrl}</a></h3>");

            writer.WriteLine(@"<div>");
            // write a span for each mermaidcolorscheme
            foreach (var style in OpenApiUrlTreeNode.MermaidNodeStyles)
            {
                writer.WriteLine($"<span style=\"padding:2px;background-color:{style.Value.Color};border: 2px solid\">{style.Key.Replace("_", " ")}</span>");
            }
            writer.WriteLine("</div>");
            writer.WriteLine("<hr/>");
            writer.WriteLine("<code class=\"language-mermaid\">");
            rootNode.WriteMermaid(writer);
            writer.WriteLine("</code>");

            // Write script tag to include JS library for rendering markdown
            writer.WriteLine(@"<script>
  var config = {
      startOnLoad:true,
      theme: 'forest',
      flowchart:{
              useMaxWidth:false,
              htmlLabels:true
          }
  };
  mermaid.initialize(config);
  window.mermaid.init(undefined, document.querySelectorAll('.language-mermaid'));
  </script>");
            // Write script tag to include JS library for rendering mermaid
            writer.WriteLine("</html");
        }

        internal async static Task PluginManifest(HidiOptions options, ILogger logger, CancellationToken cancellationToken)
        {
            // If ApiManifest is provided, set the referenced OpenAPI document
            var apiDependency = await FindApiDependency(options.FilterOptions?.FilterByApiManifest, logger, cancellationToken);
            if (apiDependency != null)
            {
                options.OpenApi = apiDependency.ApiDescripionUrl;
            }

            // Load OpenAPI document
            OpenApiDocument document = await GetOpenApi(options, logger, cancellationToken, options.MetadataVersion);

            cancellationToken.ThrowIfCancellationRequested();

            if (options.FilterOptions != null)
            {
                document = ApplyFilters(options, logger, apiDependency, null, document);
            }

            // Ensure path in options.OutputFolder exists
            var outputFolder = new DirectoryInfo(options.OutputFolder);
            if (!outputFolder.Exists)
            {
                outputFolder.Create();
            }
            // Write OpenAPI to Output folder
            options.Output = new FileInfo(Path.Combine(options.OutputFolder, "openapi.json"));
            options.TerseOutput = true;
            WriteOpenApi(options, OpenApiFormat.Json, OpenApiSpecVersion.OpenApi3_0, document, logger);

            // Create OpenAIPluginManifest from ApiDependency and OpenAPI document
            var manifest = new OpenAIPluginManifest()
            {
                NameForHuman = document.Info.Title,
                DescriptionForHuman = document.Info.Description,
                Api = new()
                {
                    Type = "openapi",
                    Url = "./openapi.json"
                }
            };
            manifest.NameForModel = manifest.NameForHuman;
            manifest.DescriptionForModel = manifest.DescriptionForHuman;

            // Write OpenAIPluginManifest to Output folder
            var manifestFile = new FileInfo(Path.Combine(options.OutputFolder, "ai-plugin.json"));
            using var file = new FileStream(manifestFile.FullName, FileMode.Create);
            using var jsonWriter = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
            manifest.Write(jsonWriter);
            jsonWriter.Flush();
        }
    }
}
