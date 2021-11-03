﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using DocFx.Plugins.ExtractSearchIndex.Lunr;

namespace DocFx.Plugins.ExtractSearchIndex
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Composition;
    using System.Collections.Immutable;

    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.MarkdownLite;
    using Microsoft.DocAsCode.Plugins;

    using HtmlAgilityPack;
    using Newtonsoft.Json;

    [Export(nameof(ExtractSearchIndex) + "Alt", typeof(IPostProcessor))]
    public class ExtractSearchIndex : IPostProcessor
    {
        private static readonly Regex RegexWhiteSpace = new Regex(@"\s+", RegexOptions.Compiled);

        public string Name => nameof(ExtractSearchIndex);
        public const string IndexFileName = "index";

        public string LunrTokenSeparator;

        public string LunrRef;

        public Dictionary<string, object> LunrFields;

        public List<string> LunrStopWords;

        public List<string> LunrMetadataWhitelist;

        public ImmutableDictionary<string, object> PrepareMetadata(ImmutableDictionary<string, object> metadata)
        {
            if (!metadata.ContainsKey("_enableSearch"))
            {
                metadata = metadata.Add("_enableSearch", true);
            }

            if (metadata.TryGetValue("_lunrTokenSeparator", out var lunrTokenSeparator))
            {
                LunrTokenSeparator = (string)lunrTokenSeparator;
            }

            if (metadata.TryGetValue("_lunrRef", out var lunrRef))
            {
                LunrRef = (string)lunrRef;
            }

            if (metadata.TryGetValue("_lunrFields", out var lunrFields))
            {
                LunrFields = (Dictionary<string, object>)lunrFields;
            }

            if (metadata.TryGetValue("_lunrStopWords", out var lunrStopWords))
            {
                LunrStopWords = (List<string>)lunrStopWords;
            }

            if (metadata.TryGetValue("_lunrStopWords", out var lunrMetadataWhitelist))
            {
                LunrMetadataWhitelist = (List<string>)lunrMetadataWhitelist;
            }

            return metadata;
        }

        public Manifest Process(Manifest manifest, string outputFolder)
        {
            if (outputFolder == null)
            {
                throw new ArgumentException("Base directory can not be null");
            }
            var indexData = new SortedDictionary<string, SearchIndexItem>();
            var indexDataFilePath = Path.Combine(outputFolder, IndexFileName + ".json");
            var htmlFiles = (from item in manifest.Files ?? Enumerable.Empty<ManifestItem>()
                from output in item.OutputFiles
                where item.DocumentType != "Toc" && output.Key.Equals(".html", StringComparison.OrdinalIgnoreCase)
                select (output.Value.RelativePath, item)).ToList();
            if (htmlFiles.Count == 0)
            {
                return manifest;
            }

            Logger.LogInfo($"Extracting index data from {htmlFiles.Count} html files");
            foreach (var (relativePath, item) in htmlFiles)
            {
                var filePath = Path.Combine(outputFolder, relativePath);
                var html = new HtmlDocument();
                Logger.LogDiagnostic($"Extracting index data from {filePath}");

                if (EnvironmentContext.FileAbstractLayer.Exists(filePath))
                {
                    try
                    {
                        using var stream = EnvironmentContext.FileAbstractLayer.OpenRead(filePath);
                        html.Load(stream, Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"Warning: Can't load content from {filePath}: {ex.Message}");
                        continue;
                    }
                    var indexItem = ExtractItem(html, relativePath, item);
                    if (indexItem != null)
                    {
                        indexData[relativePath] = indexItem;
                    }
                }
            }
            JsonUtility.Serialize(indexDataFilePath, indexData);

            // add index.json to manifest as resource file
            var manifestItem = new ManifestItem
            {
                DocumentType = "Resource",
            };
            manifestItem.OutputFiles.Add("resource", new OutputFileInfo
            {
                RelativePath = PathUtility.MakeRelativePath(outputFolder, indexDataFilePath),
            });
            manifest.Files?.Add(manifestItem);

            var lunrIndex = Lunr.Lunr.Main(builder =>
            {
                builder.Pipeline.Remove(Stemmer.Instance.Run);
                builder.SearchPipeline.Remove(Stemmer.Instance.Run);

                try
                {
                    var _ = Regex.IsMatch("__dummy__", LunrTokenSeparator);
                    Tokenizer.TokenSeparator = LunrTokenSeparator;
                }
                catch(ArgumentException)
                {
                    Logger.LogWarning("[Lunr]Warning: Invalid token separator provided, fallback to default");
                }

                if (LunrStopWords != null)
                {
                    StopWordFilter.CustomStopWords.AddRange(LunrStopWords);
                }
                else
                {
                    Logger.LogDiagnostic("[Lunr]No custom search stop words provided, skipping...");
                }

                if (LunrMetadataWhitelist != null)
                {
                    builder.MetadataWhitelist.AddRange(LunrMetadataWhitelist);
                }
                else
                {
                    Logger.LogDiagnostic("[Lunr]No metadata whitelist provided, skipping...");
                }

                if (!string.IsNullOrEmpty(LunrRef))
                {
                    builder.Ref(LunrRef);
                }

                if (LunrFields != null)
                {
                    foreach (var field in LunrFields)
                    {
                        builder.Field(field.Key, field.Value.ToJsonString().FromJsonString<FieldRef.FieldMetadata>());
                    }
                }
                else
                {
                    Logger.LogWarning("[Lunr]No fields provided, this may yield strange results");
                }

                foreach (var doc in indexData)
                {
                    builder.Add(doc.Value, new FieldRef.FieldMetadata());
                }
            });
            var indexDataFilePath2 = Path.Combine(outputFolder, "search-index.json");
            JsonUtility.Serialize(indexDataFilePath2, lunrIndex.ToJson());

            // add index_lunr.json to manifest as resource file
            manifestItem = new ManifestItem
            {
                DocumentType = "Resource",
            };
            manifestItem.OutputFiles.Add("resource", new OutputFileInfo
            {
                RelativePath = PathUtility.MakeRelativePath(outputFolder, indexDataFilePath2),
            });
            manifest.Files?.Add(manifestItem);

            return manifest;
        }

        internal SearchIndexItem ExtractItem(HtmlDocument html, string href, ManifestItem item)
        {
            var contentBuilder = new StringBuilder();

            if (html.DocumentNode.SelectNodes("/html/head/meta[@name='searchOption' and @content='noindex']") != null)
            {
                return null;
            }

            // Select content between the data-searchable class tag
            var nodes = html.DocumentNode.SelectNodes("//*[contains(@class,'data-searchable')]") ?? Enumerable.Empty<HtmlNode>();
            // Select content between the article tag
            nodes = nodes.Union(html.DocumentNode.SelectNodes("//article") ?? Enumerable.Empty<HtmlNode>());
            foreach (var node in nodes)
            {
                ExtractTextFromNode(node, contentBuilder);
            }

            var content = NormalizeContent(contentBuilder.ToString());
            var title = ExtractTitleFromHtml(html);
            var langs = ExtractLanguagesFromHtml(html, item);
            var type = item.DocumentType == "Conceptual" ? "article" : "api";

            return new SearchIndexItem { Type = type, Href = href, Title = title, Keywords = content, Languages = langs};
        }

        private string ExtractTitleFromHtml(HtmlDocument html)
        {
            var titleNode = html.DocumentNode.SelectSingleNode("//head/title");
            var originalTitle = titleNode?.InnerText;
            return NormalizeContent(originalTitle);
        }

        private string ExtractLanguagesFromHtml(HtmlDocument html, ManifestItem item)
        {
            if (item.DocumentType != "Conceptual") return NormalizeLanguages((List<string>)item.Metadata["langs"]);
            var codeNodes = html.DocumentNode.SelectNodes("//*/pre/code");
            if (codeNodes == null) return "";
            return NormalizeLanguages(codeNodes.Select(FilterLanguages).Where(el => el != null));
        }

        private static string FilterLanguages(HtmlNode el)
        {
            var classList = el.Attributes["class"]?.Value.Split(' ');
            return classList?.First(el2 => el2.StartsWith("lang-")).Substring(5);
        }

        private string NormalizeLanguages(IEnumerable<string> list)
        {
            var newList = new List<string>();
            foreach (var lang in list)
            {
                switch (lang.ToLower())
                {
                    case "csharp":
                    case "cs":
                        newList.Add("csharp");
                        newList.Add("cs");
                        break;
                    case "typescript":
                    case "ts":
                        newList.Add("typescript");
                        newList.Add("ts");
                        newList.Add("javascript");
                        newList.Add("js");
                        break;
                    default:
                        newList.Add(lang);
                        break;
                }
            }

            return string.Join(" ", newList.Distinct());
        }

        private string NormalizeContent(string str)
        {
            if (string.IsNullOrEmpty(str))
            {
                return string.Empty;
            }
            str = StringHelper.HtmlDecode(str);
            return RegexWhiteSpace.Replace(str, " ").Trim();
        }

        private void ExtractTextFromNode(HtmlNode root, StringBuilder contentBuilder)
        {
            if (root == null)
            {
                return;
            }

            if (!root.HasChildNodes)
            {
                contentBuilder.Append(root.InnerText);
                contentBuilder.Append(" ");
            }
            else
            {
                foreach (var node in root.ChildNodes)
                {
                    ExtractTextFromNode(node, contentBuilder);
                }
            }
        }
    }
}
