﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Web.Caching;
using System.Web.Hosting;
using System.Web.WebPages;
using HtmlAgilityPack;
using MarkdownSharp;

namespace NuGet.Docs
{
    /// <summary>
    /// Each markdown page is a web page that has this harcoded logic
    /// </summary>
    public class MarkdownWebPage : WebPage
    {
        // Set the cache timeout to 1 day (we'll also have cache dependencies)
        private const int CacheTimeout = 24 * 60 * 60;
        private const string OutlineLayout = "~/_Layout-Outline.cshtml";
        private static List<string> _virtualPathDependencies = new List<string>
        {
            "~/_PageStart.cshtml", 
            "~/_Layout.cshtml",
            OutlineLayout
        };

        public override void ExecutePageHierarchy()
        {
            this.Layout = OutlineLayout;
            base.ExecutePageHierarchy();
        }

        public override void Execute()
        {
            InitalizeCache();

            Page.Title = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(Path.GetFileNameWithoutExtension(VirtualPath).Replace('-', ' ')).Replace("Nuget", "NuGet");
            Page.Source = GetSourcePath();
            Page.GeneratedDateTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss tt UTC");

            // Get the page content
            string markdownContent = GetMarkdownContent();

            // Transform the markdown
            string transformed = Transform(markdownContent);

            // Write the raw html it to the response (unencoded)
            WriteLiteral(transformed);
        }

        private void InitalizeCache()
        {
            _virtualPathDependencies.Add(VirtualPath);
            CacheDependency cd = HostingEnvironment.VirtualPathProvider.GetCacheDependency(VirtualPath, _virtualPathDependencies.ToArray(), DateTime.UtcNow);
            Response.AddCacheDependency(cd);
            Response.OutputCache(CacheTimeout);
        }

        /// <summary>
        /// Transforms the raw markdown content into html
        /// </summary>
        private string Transform(string content)
        {
            var githubMarkdown = new Octokit.MiscellaneousClient(new Octokit.Connection(new Octokit.ProductHeaderValue("NuGet.Docs")));
            string fileContents = null;

            try
            {
                // Try to transform the content using GitHub's API
                var request = githubMarkdown.RenderRawMarkdown(content);
                request.Wait();

                if (request.IsCompleted)
                {
                    fileContents = request.Result;
                    Page.Generator = "GitHub";
                }
            }
            catch
            {
                // If the call to GitHub failed, then we'll swallow the exception
                // and in the finally block, we'll use MarkdownSharp as a fallback.
            }
            finally
            {
                if (fileContents == null)
                {
                    fileContents = new Markdown().Transform(content);
                    Page.Generator = "MarkdownSharp";
                }
            }

            return ProcessTableOfContents(fileContents);
        }

        /// <summary>
        /// Takes HTML and parses out all heading and sets IDs for each heading. Then sets the Headings property on the page.
        /// </summary>
        private string ProcessTableOfContents(string content)
        {
            var doc = new HtmlDocument();
            doc.OptionUseIdAttribute = true;
            doc.LoadHtml(content);

            var allNodes = doc.DocumentNode.Descendants();
            var allHeadingNodes = allNodes
                .Where(node =>
                    node.Name.Length == 2
                    && node.Name.StartsWith("h", System.StringComparison.InvariantCultureIgnoreCase)
                    && Char.IsDigit(node.Name[1]));

            var headings = new List<Heading>();
            var containerDictionary = new Dictionary<HtmlNode, IEnumerable<HtmlNode>>();
            foreach (var heading in allHeadingNodes)
            {
                string id = heading.InnerText.Replace(" ", "-").ToLowerInvariant(); ;

                // GitHub gives us anchors in the headings, MarkdownSharp doesn't
                HtmlNode anchor = heading.SelectSingleNode("a");

                if (anchor != null)
                {
                    // Note that the text of the heading is not within the anchor
                    // Get the name of the anchor as our id (but provide our existing id as the default)
                    id = anchor.GetAttributeValue("name", id);

                    // GitHub likes to prefix the names with: user-content- but we'll strip that off
                    if (id.StartsWith("user-content-"))
                    {
                        id = id.Substring(13);
                    }
                }
                else
                {
                    // Create our anchor
                    anchor = HtmlAgilityPack.HtmlNode.CreateNode("<a></a>");
                    heading.ChildNodes.Insert(0, anchor);
                }

                // Skip the heading if the id ended up empty somehow (like an empty heading)
                if (id != null)
                {
                    anchor.SetAttributeValue("name", HttpUtility.HtmlAttributeEncode(id.ToLowerInvariant().Trim()));
                    var headingLevel = int.Parse(heading.Name[1].ToString());

                    // When encountering a heading element, wrap it in a HTML container and apply a CSS class.
                    if (headingLevel == 1)
                    {
                        BuildHeadingDiv(heading, containerDictionary, cssClass: "topic");
                    }
                    else if (headingLevel == 2)
                    {
                        BuildHeadingDiv(heading, containerDictionary, cssClass: "sub-topic");
                    }

                    headings.Add(new Heading(id, headingLevel, heading.InnerText));
                }
            }

            PostProcessHeadingContainers(containerDictionary);

            Page.Headings = headings;

            var docteredHTML = new StringWriter();
            doc.Save(docteredHTML);
            return docteredHTML.ToString();
        }

        /// <summary>
        /// Build up a dictionary to allow post-processing of the HTML node collection being enumerated.
        /// </summary>
        /// <param name="heading">The HTML heading node.</param>
        /// <param name="containerDictionary">The dictionary to add the wrapping HTML container and its elements into.</param>
        /// <param name="cssClass">The CSS class to be applied to the wrapping HTML container.</param>
        private static void BuildHeadingDiv(HtmlNode heading, Dictionary<HtmlNode, IEnumerable<HtmlNode>> containerDictionary, string cssClass)
        {
            var div = HtmlAgilityPack.HtmlNode.CreateNode(string.Format("<div class=\"{0}\"></div>", cssClass));

            var elementsToMove = new List<HtmlNode>();
            elementsToMove.Add(heading);

            // All elements after the heading element should be wrapped within the same container, until the next heading is encountered (any level).
            var nextElement = heading.NextSibling;

            while (nextElement != null &&
                   !(nextElement.Name.Length == 2
                     && nextElement.Name.StartsWith("h", System.StringComparison.InvariantCultureIgnoreCase)
                     && Char.IsDigit(nextElement.Name[1])))
            {
                elementsToMove.Add(nextElement);

                nextElement = nextElement.NextSibling;
            }

            containerDictionary.Add(div, elementsToMove);
        }

        /// <summary>
        /// Wrap heading content in container divs
        /// </summary>
        /// <param name="containerDictionary">The dictionary of heading elements and their contents.</param>
        private static void PostProcessHeadingContainers(Dictionary<HtmlNode, IEnumerable<HtmlNode>> containerDictionary)
        {
            foreach (var nodes in containerDictionary)
            {
                var div = nodes.Key;
                var heading = nodes.Value.First();
                var parentNode = heading.ParentNode;

                div.AppendChild(heading);
                foreach (HtmlNode element in nodes.Value.Skip(1))
                {
                    div.AppendChild(element);
                    parentNode.RemoveChild(element);
                }

                parentNode.ReplaceChild(div, heading);
            }
        }

        /// <summary>
        /// Turns app relative urls (~/foo/bar) into the resolved version of that url for this page.
        /// </summary>
        private string ProcessUrls(string content)
        {
            return Regex.Replace(content, @"\((?<url>~/.*?)\)", match => "(" + Href(match.Groups["url"].Value) + ")");
        }

        /// <summary>
        /// Returns the markdown content within this page
        /// </summary>
        private string GetMarkdownContent()
        {
            VirtualFile file = HostingEnvironment.VirtualPathProvider.GetFile(VirtualPath);
            Stream stream = file.Open();
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        /// <summary>
        /// Returns the source file path for the virtual path on the request, with case sensitivity.
        /// </summary>
        /// <remarks>
        /// It's a shame nothing in the framework seems to do this for the path as a whole.  FileInfo
        /// and DirectoryInfo, among others, return the path using the casing specified.  And
        /// VirtualPathProvider.GetDirectory does not return the correct casing, but GetFile does.
        /// <para>
        /// So, we walk up the path and get the case sensitive name for each segment and then piece
        /// it all back together.
        /// </para>
        /// </remarks>
        private string GetSourcePath()
        {
            string requestFilePath = VirtualPath;
            Stack<string> pathSegments = new Stack<string>();

            do
            {
                VirtualFile segment = HostingEnvironment.VirtualPathProvider.GetFile(requestFilePath);

                if (segment != null && segment.Name != null)
                {
                    pathSegments.Push(segment.Name);
                }

                int lastSlash = requestFilePath.LastIndexOf('/');
                if (lastSlash > 0)
                {
                    requestFilePath = requestFilePath.Substring(0, lastSlash);
                }
                else
                {
                    break;
                }
            }
            while (requestFilePath != null && requestFilePath.Length > 1 && requestFilePath.Contains('/'));

            return String.Join("/", pathSegments);
        }
    }
}
