﻿using AngleSharp.Dom;
using AngleSharp.Dom.Html;
using AngleSharp.Parser.Html;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.WebParts;
using SharePointPnP.Modernization.Framework.Entities;
using SharePointPnP.Modernization.Framework.Telemetry;
using SharePointPnP.Modernization.Framework.Transform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using System.Xml.XPath;

namespace SharePointPnP.Modernization.Framework.Pages
{

    /// <summary>
    /// Base class for the page analyzers
    /// </summary>
    public abstract class BasePage: BaseTransform
    {
        internal class WebPartPlaceHolder
        {
            public string Id { get; set; }
            public string ControlId { get; set; }
            public int Row { get; set; }
            public int Column { get; set; }
            public int Order { get; set; }
            public WebPartDefinition WebPartDefinition { get; set; }
            public ClientResult<string> WebPartXml { get; set; }
            public string WebPartType { get; set; }
        }

        internal HtmlParser parser;
        private const string webPartMarkerString = "[[WebPartMarker]]";

        public ListItem page;
        public ClientContext cc;
        public PageTransformation pageTransformation;

        #region construction
        /// <summary>
        /// Constructs the base page class instance
        /// </summary>
        /// <param name="page">page ListItem</param>
        /// <param name="pageTransformation">page transformation model to use for extraction or transformation</param>
        public BasePage(ListItem page, PageTransformation pageTransformation, IList<ILogObserver> logObservers = null)
        {
            // Register observers
            if (logObservers != null)
            {
                foreach (var observer in logObservers)
                {
                    base.RegisterObserver(observer);
                }
            }

            this.page = page;
            this.cc = (page.Context as ClientContext);
            this.cc.RequestTimeout = Timeout.Infinite;

            this.pageTransformation = pageTransformation;
            this.parser = new HtmlParser();
        }
        #endregion

        /// <summary>
        /// Get's the type of the web part
        /// </summary>
        /// <param name="webPartXml">Web part xml to analyze</param>
        /// <returns>Type of the web part as fully qualified name</returns>
        public string GetType(string webPartXml)
        {
            string type = "Unknown";

            if (!string.IsNullOrEmpty(webPartXml))
            {
                var xml = XElement.Parse(webPartXml);
                var xmlns = xml.XPathSelectElement("*").GetDefaultNamespace();
                if (xmlns.NamespaceName.Equals("http://schemas.microsoft.com/WebPart/v3", StringComparison.InvariantCultureIgnoreCase))
                {
                    type = xml.Descendants(xmlns + "type").FirstOrDefault().Attribute("name").Value;
                }
                else if (xmlns.NamespaceName.Equals("http://schemas.microsoft.com/WebPart/v2", StringComparison.InvariantCultureIgnoreCase))
                {
                    type = $"{xml.Descendants(xmlns + "TypeName").FirstOrDefault().Value}, {xml.Descendants(xmlns + "Assembly").FirstOrDefault().Value}";
                }
            }

            return type;
        }

        internal void AnalyzeWikiContentBlock(List<WebPartEntity> webparts, IHtmlDocument htmlDoc, List<WebPartPlaceHolder> webPartsToRetrieve, int rowCount, int colCount, IElement content)
        {           
            // Drop elements which we anyhow can't transform and/or which are stripped out from RTE
            CleanHtml(content, htmlDoc);

            StringBuilder textContent = new StringBuilder();
            int order = 0;
            foreach (var node in content.ChildNodes)
            {
                // Do we find a web part inside...
                if (((node as IHtmlElement) != null) && ContainsWebPart(node as IHtmlElement))
                {
                    var extraText = StripWebPart(node as IHtmlElement);
                    string extraTextAfterWebPart = null;
                    string extraTextBeforeWebPart = null;
                    if (!string.IsNullOrEmpty(extraText))
                    {
                        // Should be, but checking anyhow
                        int webPartMarker = extraText.IndexOf(webPartMarkerString);
                        if (webPartMarker > -1)
                        {
                            extraTextBeforeWebPart = extraText.Substring(0, webPartMarker);
                            extraTextAfterWebPart = extraText.Substring(webPartMarker + webPartMarkerString.Length);

                            // there could have been multiple web parts in a row (we don't support text inbetween them for now)...strip the remaining markers
                            extraTextBeforeWebPart = extraTextBeforeWebPart.Replace(webPartMarkerString, "");
                            extraTextAfterWebPart = extraTextAfterWebPart.Replace(webPartMarkerString, "");
                        }
                    }

                    if (!string.IsNullOrEmpty(extraTextBeforeWebPart))
                    {
                        textContent.AppendLine(extraTextBeforeWebPart);
                    }

                    // first insert text part (if it was available)
                    if (!string.IsNullOrEmpty(textContent.ToString()))
                    {
                        order++;
                        webparts.Add(CreateWikiTextPart(textContent.ToString(), rowCount, colCount, order));
                        textContent.Clear();
                    }

                    // then process the web part
                    order++;
                    Regex regexClientIds = new Regex(@"id=\""div_(?<ControlId>(\w|\-)+)");
                    if (regexClientIds.IsMatch((node as IHtmlElement).OuterHtml))
                    {
                        foreach (Match webPartMatch in regexClientIds.Matches((node as IHtmlElement).OuterHtml))
                        {
                            // Store the web part we need, will be retrieved afterwards to optimize performance
                            string serverSideControlId = webPartMatch.Groups["ControlId"].Value;
                            var serverSideControlIdToSearchFor = $"g_{serverSideControlId.Replace("-", "_")}";
                            webPartsToRetrieve.Add(new WebPartPlaceHolder() { ControlId = serverSideControlIdToSearchFor, Id = serverSideControlId, Row = rowCount, Column = colCount, Order = order });
                        }
                    }

                    // Process the extra text that was positioned after the web part (if any)
                    if (!string.IsNullOrEmpty(extraTextAfterWebPart))
                    {
                        textContent.AppendLine(extraTextAfterWebPart);
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(node.TextContent.Trim()) && node.TextContent.Trim() == "\n")
                    {
                        // ignore, this one is typically added after a web part
                    }
                    else
                    {
                        if (node.HasChildNodes)
                        {
                            textContent.AppendLine((node as IHtmlElement).OuterHtml);
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(node.TextContent.Trim()))
                            {
                                textContent.AppendLine(node.TextContent);
                            }
                            else
                            {
                                if (node.NodeName.Equals("br", StringComparison.InvariantCultureIgnoreCase))
                                {
                                    textContent.AppendLine("<BR>");
                                }
                                // given that wiki html can contain embedded images and videos while not having child nodes we need include these.
                                // case: img/iframe tag as "only" element to evaluate (e.g. first element in the contenthost)
                                else if (node.NodeName.Equals("img", StringComparison.InvariantCultureIgnoreCase) ||
                                         node.NodeName.Equals("iframe", StringComparison.InvariantCultureIgnoreCase))
                                {
                                    textContent.AppendLine((node as IHtmlElement).OuterHtml);
                                }
                            }
                        }
                    }
                }
            }

            // there was only one text part
            if (!string.IsNullOrEmpty(textContent.ToString()))
            {
                // insert text part to the web part collection
                order++;
                webparts.Add(CreateWikiTextPart(textContent.ToString(), rowCount, colCount, order));
            }
        }

        internal void LoadWebPartsInWikiContentFromServer(List<WebPartEntity> webparts, File wikiPage, List<WebPartPlaceHolder> webPartsToRetrieve)
        {
            // Load web part manager and use it to load each web part
            LimitedWebPartManager limitedWPManager = wikiPage.GetLimitedWebPartManager(PersonalizationScope.Shared);
            cc.Load(limitedWPManager);

            foreach (var webPartToRetrieve in webPartsToRetrieve)
            {
                // Check if the web part was loaded when we loaded the web parts collection via the web part manager
                if (!Guid.TryParse(webPartToRetrieve.Id, out Guid webPartToRetrieveGuid))
                {
                    // Skip since guid is not valid
                    continue;
                }

                // Sometimes the returned wiki html contains web parts which are not anymore on the page...using the ExceptionHandlingScope 
                // we can handle these errors server side while just doing a single roundtrip
                var scope = new ExceptionHandlingScope(cc);
                using (scope.StartScope())
                {
                    using (scope.StartTry())
                    {
                        webPartToRetrieve.WebPartDefinition = limitedWPManager.WebParts.GetByControlId(webPartToRetrieve.ControlId);
                        cc.Load(webPartToRetrieve.WebPartDefinition, wp => wp.Id, wp => wp.WebPart.ExportMode, wp => wp.WebPart.Title, wp => wp.WebPart.ZoneIndex, wp => wp.WebPart.IsClosed, wp => wp.WebPart.Hidden, wp => wp.WebPart.Properties);
                    }
                    using (scope.StartCatch())
                    {

                    }
                }
            }
            cc.ExecuteQueryRetry();


            // Load the web part XML for the web parts that do allow it
            bool isDirty = false;
            foreach (var webPartToRetrieve in webPartsToRetrieve)
            {
                // Important to only process the web parts that did not return an error in the previous server call
                if (webPartToRetrieve.WebPartDefinition != null && (webPartToRetrieve.WebPartDefinition.ServerObjectIsNull.HasValue && webPartToRetrieve.WebPartDefinition.ServerObjectIsNull.Value == false))
                {
                    // Retry to load the properties, sometimes they're not retrieved
                    webPartToRetrieve.WebPartDefinition.EnsureProperty(wp => wp.Id);
                    webPartToRetrieve.WebPartDefinition.WebPart.EnsureProperties(wp => wp.ExportMode, wp => wp.Title, wp => wp.ZoneIndex, wp => wp.IsClosed, wp => wp.Hidden, wp => wp.Properties);

                    if (webPartToRetrieve.WebPartDefinition.WebPart.ExportMode == WebPartExportMode.All)
                    {
                        webPartToRetrieve.WebPartXml = limitedWPManager.ExportWebPart(webPartToRetrieve.WebPartDefinition.Id);
                        isDirty = true;
                    }
                }
            }
            if (isDirty)
            {
                cc.ExecuteQueryRetry();
            }

            // Determine the web part type and store it in the web parts array
            foreach (var webPartToRetrieve in webPartsToRetrieve)
            {
                if (webPartToRetrieve.WebPartDefinition != null && (webPartToRetrieve.WebPartDefinition.ServerObjectIsNull.HasValue && webPartToRetrieve.WebPartDefinition.ServerObjectIsNull.Value == false))
                {
                    // Important to only process the web parts that did not return an error in the previous server call
                    if (webPartToRetrieve.WebPartDefinition.WebPart.ExportMode != WebPartExportMode.All)
                    {
                        // Use different approach to determine type as we can't export the web part XML without indroducing a change
                        webPartToRetrieve.WebPartType = GetTypeFromProperties(webPartToRetrieve.WebPartDefinition.WebPart.Properties);
                    }
                    else
                    {
                        webPartToRetrieve.WebPartType = GetType(webPartToRetrieve.WebPartXml.Value);
                    }

                    webparts.Add(new WebPartEntity()
                    {
                        Title = webPartToRetrieve.WebPartDefinition.WebPart.Title,
                        Type = webPartToRetrieve.WebPartType,
                        Id = webPartToRetrieve.WebPartDefinition.Id,
                        ServerControlId = webPartToRetrieve.Id,
                        Row = webPartToRetrieve.Row,
                        Column = webPartToRetrieve.Column,
                        Order = webPartToRetrieve.Order,
                        ZoneId = "",
                        ZoneIndex = (uint)webPartToRetrieve.WebPartDefinition.WebPart.ZoneIndex,
                        IsClosed = webPartToRetrieve.WebPartDefinition.WebPart.IsClosed,
                        Hidden = webPartToRetrieve.WebPartDefinition.WebPart.Hidden,
                        Properties = Properties(webPartToRetrieve.WebPartDefinition.WebPart.Properties, webPartToRetrieve.WebPartType, webPartToRetrieve.WebPartXml == null ? "" : webPartToRetrieve.WebPartXml.Value),
                    });
                }
            }
        }


        /// <summary>
        /// Stores text content as a fake web part
        /// </summary>
        /// <param name="wikiTextPartContent">Text to store</param>
        /// <param name="row">Row of the fake web part</param>
        /// <param name="col">Column of the fake web part</param>
        /// <param name="order">Order inside the row/column</param>
        /// <returns>A web part entity to add to the collection</returns>
        internal WebPartEntity CreateWikiTextPart(string wikiTextPartContent, int row, int col, int order)
        {
            Dictionary<string, string> properties = new Dictionary<string, string>();
            properties.Add("Text", wikiTextPartContent.Trim().Replace("\r\n", string.Empty));

            return new WebPartEntity()
            {
                Title = "WikiText",
                Type = "SharePointPnP.Modernization.WikiTextPart",
                Id = Guid.Empty,
                Row = row,
                Column = col,
                Order = order,
                Properties = properties,
            };
        }

        private void CleanHtml(IElement element, IHtmlDocument document)
        {
            foreach (var node in element.QuerySelectorAll("*").ToList())
            {
                if (node.ParentElement != null && IsUntransformableBlockElement(node))
                {
                    // create new div node and add all current children to it
                    var div = document.CreateElement("div");
                    foreach (var child in node.ChildNodes.ToList())
                    {
                        div.AppendChild(child);
                    }
                    // replace the unsupported node with the new div
                    node.ParentElement.ReplaceChild(div, node);
                }
            }
        }

        private bool IsUntransformableBlockElement(IElement element)
        {
            var tag = element.TagName.ToLower();
            if (tag == "article" ||
                tag == "address" ||
                tag == "aside" ||
                tag == "canvas" ||
                tag == "dd" ||
                tag == "dl" ||
                tag == "dt" ||
                tag == "fieldset" ||
                tag == "figcaption" ||
                tag == "figure" ||
                tag == "footer" ||
                tag == "form" ||
                tag == "header" ||
                //tag == "hr" || // will be replaced at in the html transformator
                tag == "main" ||
                tag == "nav" ||
                tag == "noscript" ||
                tag == "output" ||
                tag == "pre" ||
                tag == "section" ||
                tag == "tfoot" ||
                tag == "video" ||
                tag == "aside")
            {
                return true;
            }

            return false;
        }


        /// <summary>
        /// Does the tree of nodes somewhere contain a web part?
        /// </summary>
        /// <param name="element">Html content to analyze</param>
        /// <returns>True if it contains a web part</returns>
        private bool ContainsWebPart(IHtmlElement element)
        {
            var doc = parser.Parse(element.OuterHtml);
            var nodes = doc.All.Where(p => p.LocalName == "div");
            foreach (var node in nodes)
            {
                if (((node as IHtmlElement) != null) && (node as IHtmlElement).ClassList.Contains("ms-rte-wpbox"))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Strips the div holding the web part from the html
        /// </summary>
        /// <param name="element">Html element holding one or more web part divs</param>
        /// <returns>Cleaned html with a placeholder for the web part div</returns>
        private string StripWebPart(IHtmlElement element)
        {
            IElement copy = element.Clone(true) as IElement;
            var doc = parser.Parse(copy.OuterHtml);
            var nodes = doc.All.Where(p => p.LocalName == "div");
            if (nodes.Count() > 0)
            {
                foreach (var node in nodes.ToList())
                {
                    if (((node as IHtmlElement) != null) && (node as IHtmlElement).ClassList.Contains("ms-rte-wpbox"))
                    {
                        var newElement = doc.CreateTextNode(webPartMarkerString);
                        node.Parent.ReplaceChild(newElement, node);
                    }
                }

                if (doc.DocumentElement.Children[1].FirstElementChild != null &&
                    doc.DocumentElement.Children[1].FirstElementChild is IHtmlDivElement)
                {
                    return doc.DocumentElement.Children[1].FirstElementChild.InnerHtml;
                }
                else
                {
                    return doc.DocumentElement.Children[1].InnerHtml;
                }
            }
            else
            {
                return null;
            }
        }


        /// <summary>
        /// Get's the type of the web part by detecting if from the available properties
        /// </summary>
        /// <param name="properties">Web part properties to analyze</param>
        /// <returns>Type of the web part as fully qualified name</returns>
        public string GetTypeFromProperties(PropertyValues properties)
        {
            // Check for XSLTListView web part
            string[] xsltWebPart = new string[] { "ListUrl", "ListId", "Xsl", "JSLink", "ShowTimelineIfAvailable" };                        
            if (CheckWebPartProperties(xsltWebPart, properties))
            {
                return WebParts.XsltListView;
            }

            // Check for ListView web part
            string[] listWebPart = new string[] { "ListViewXml", "ListName", "ListId", "ViewContentTypeId", "PageType" };
            if (CheckWebPartProperties(listWebPart, properties))
            {
                return WebParts.ListView;
            }

            // check for Media web part
            string[] mediaWebPart = new string[] { "AutoPlay", "MediaSource", "Loop", "IsPreviewImageSourceOverridenForVideoSet", "PreviewImageSource" };
            if (CheckWebPartProperties(mediaWebPart, properties))
            {
                return WebParts.Media;
            }

            // check for SlideShow web part
            string[] slideShowWebPart = new string[] { "LibraryGuid", "Layout", "Speed", "ShowToolbar", "ViewGuid" };
            if (CheckWebPartProperties(slideShowWebPart, properties))
            {
                return WebParts.PictureLibrarySlideshow;
            }

            // check for Chart web part
            string[] chartWebPart = new string[] { "ConnectionPointEnabled", "ChartXml", "DataBindingsString", "DesignerChartTheme" };
            if (CheckWebPartProperties(chartWebPart, properties))
            {
                return WebParts.Chart;
            }

            // check for Site Members web part
            string[] membersWebPart = new string[] { "NumberLimit", "DisplayType", "MembershipGroupId", "Toolbar" };
            if (CheckWebPartProperties(membersWebPart, properties))
            {
                return WebParts.Members;
            }

            // check for Silverlight web part
            string[] silverlightWebPart = new string[] { "MinRuntimeVersion", "WindowlessMode", "CustomInitParameters", "Url", "ApplicationXml" };
            if (CheckWebPartProperties(silverlightWebPart, properties))
            {
                return WebParts.Silverlight;
            }

            // check for Add-in Part web part
            string[] addinPartWebPart = new string[] { "FeatureId", "ProductWebId", "ProductId" };
            if (CheckWebPartProperties(addinPartWebPart, properties))
            {
                return WebParts.Client;
            }

            // check for Script Editor web part
            string[] scriptEditorWebPart = new string[] { "Content"};
            if (CheckWebPartProperties(scriptEditorWebPart, properties))
            {
                return WebParts.ScriptEditor;
            }

            // This needs to be last, but we still pages with sandbox user code web parts on them
            string[] sandboxWebPart = new string[] { "CatalogIconImageUrl", "AllowEdit", "TitleIconImageUrl", "ExportMode" };
            if (CheckWebPartProperties(sandboxWebPart, properties))
            {
                return WebParts.SPUserCode;
            }

            return "NonExportable_Unidentified";
        }

        private bool CheckWebPartProperties(string[] propertiesToCheck, PropertyValues properties)
        {
            bool isWebPart = true;
            foreach (var wpProp in propertiesToCheck)
            {
                if (!properties.FieldValues.ContainsKey(wpProp))
                {
                    isWebPart = false;
                    break;
                }
            }

            return isWebPart;
        }

        /// <summary>
        /// Checks the PageTransformation XML data to know which properties need to be kept for the given web part and collects their values
        /// </summary>
        /// <param name="properties">Properties collection retrieved when we loaded the web part</param>
        /// <param name="webPartType">Type of the web part</param>
        /// <param name="webPartXml">Web part XML</param>
        /// <returns>Collection of the requested property/value pairs</returns>
        public Dictionary<string, string> Properties(PropertyValues properties, string webPartType, string webPartXml)
        {
            Dictionary<string, string> propertiesToKeep = new Dictionary<string, string>();

            List<Property> propertiesToRetrieve = this.pageTransformation.BaseWebPart.Properties.ToList<Property>();
            var webPartProperties = this.pageTransformation.WebParts.Where(p => p.Type.Equals(webPartType, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
            if (webPartProperties != null && webPartProperties.Properties != null)
            {
                propertiesToRetrieve.AddRange(webPartProperties.Properties.ToList<Property>());
            }

            if (string.IsNullOrEmpty(webPartXml))
            {
                if (webPartType == WebParts.Client)
                {
                    // Special case since we don't know upfront which properties are relevant here...so let's take them all
                    foreach(var prop in properties.FieldValues)
                    {
                        propertiesToKeep.Add(prop.Key, prop.Value != null ? prop.Value.ToString() : "");
                    }
                }
                else
                {
                    // Special case where we did not have export rights for the web part XML, assume this is a V3 web part
                    foreach (var property in propertiesToRetrieve)
                    {
                        if (!string.IsNullOrEmpty(property.Name) && properties.FieldValues.ContainsKey(property.Name))
                        {
                            propertiesToKeep.Add(property.Name, properties[property.Name] != null ? properties[property.Name].ToString() : "");
                        }
                    }
                }
            }
            else
            {
                var xml = XElement.Parse(webPartXml);
                var xmlns = xml.XPathSelectElement("*").GetDefaultNamespace();
                if (xmlns.NamespaceName.Equals("http://schemas.microsoft.com/WebPart/v3", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (webPartType == WebParts.Client)
                    {
                        // Special case since we don't know upfront which properties are relevant here...so let's take them all
                        foreach (var prop in properties.FieldValues)
                        {
                            propertiesToKeep.Add(prop.Key, prop.Value != null ? prop.Value.ToString() : "");
                        }
                    }
                    else
                    {
                        // the retrieved properties are sufficient
                        foreach (var property in propertiesToRetrieve)
                        {
                            if (!string.IsNullOrEmpty(property.Name) && properties.FieldValues.ContainsKey(property.Name))
                            {
                                propertiesToKeep.Add(property.Name, properties[property.Name] != null ? properties[property.Name].ToString() : "");
                            }
                        }
                    }
                }
                else if (xmlns.NamespaceName.Equals("http://schemas.microsoft.com/WebPart/v2", StringComparison.InvariantCultureIgnoreCase))
                {
                    foreach (var property in propertiesToRetrieve)
                    {
                        if (!string.IsNullOrEmpty(property.Name))
                        {
                            if (properties.FieldValues.ContainsKey(property.Name))
                            {
                                propertiesToKeep.Add(property.Name, properties[property.Name] != null ? properties[property.Name].ToString() : "");
                            }
                            else
                            {
                                // check XMl for property
                                var v2Element = xml.Descendants(xmlns + property.Name).FirstOrDefault();
                                if (v2Element != null)
                                {
                                    propertiesToKeep.Add(property.Name, v2Element.Value);
                                }

                                // Some properties do have their own namespace defined
                                if (webPartType == WebParts.SimpleForm && property.Name.Equals("Content", StringComparison.InvariantCultureIgnoreCase))
                                {
                                    // Load using the http://schemas.microsoft.com/WebPart/v2/SimpleForm namespace
                                    XNamespace xmlcontentns = "http://schemas.microsoft.com/WebPart/v2/SimpleForm";
                                    v2Element = xml.Descendants(xmlcontentns + property.Name).FirstOrDefault();
                                    if (v2Element != null)
                                    {
                                        propertiesToKeep.Add(property.Name, v2Element.Value);
                                    }
                                }
                                else if (webPartType == WebParts.ContentEditor)
                                {
                                    if (property.Name.Equals("ContentLink", StringComparison.InvariantCultureIgnoreCase) ||
                                        property.Name.Equals("Content", StringComparison.InvariantCultureIgnoreCase) ||
                                        property.Name.Equals("PartStorage", StringComparison.InvariantCultureIgnoreCase) )
                                    {
                                        XNamespace xmlcontentns = "http://schemas.microsoft.com/WebPart/v2/ContentEditor";
                                        v2Element = xml.Descendants(xmlcontentns + property.Name).FirstOrDefault();
                                        if (v2Element != null)
                                        {
                                            propertiesToKeep.Add(property.Name, v2Element.Value);
                                        }
                                    }
                                }
                                else if (webPartType == WebParts.Xml)
                                {
                                    if (property.Name.Equals("XMLLink", StringComparison.InvariantCultureIgnoreCase) ||
                                        property.Name.Equals("XML", StringComparison.InvariantCultureIgnoreCase) ||
                                        property.Name.Equals("XSLLink", StringComparison.InvariantCultureIgnoreCase) ||
                                        property.Name.Equals("XSL", StringComparison.InvariantCultureIgnoreCase) ||
                                        property.Name.Equals("PartStorage", StringComparison.InvariantCultureIgnoreCase))
                                    {
                                        XNamespace xmlcontentns = "http://schemas.microsoft.com/WebPart/v2/Xml";
                                        v2Element = xml.Descendants(xmlcontentns + property.Name).FirstOrDefault();
                                        if (v2Element != null)
                                        {
                                            propertiesToKeep.Add(property.Name, v2Element.Value);
                                        }
                                    }
                                }
                                else if (webPartType == WebParts.SiteDocuments)
                                {
                                    if (property.Name.Equals("UserControlledNavigation", StringComparison.InvariantCultureIgnoreCase) ||
                                        property.Name.Equals("ShowMemberships", StringComparison.InvariantCultureIgnoreCase) ||
                                        property.Name.Equals("UserTabs", StringComparison.InvariantCultureIgnoreCase))
                                    {
                                        XNamespace xmlcontentns = "urn:schemas-microsoft-com:sharepoint:portal:sitedocumentswebpart";
                                        v2Element = xml.Descendants(xmlcontentns + property.Name).FirstOrDefault();
                                        if (v2Element != null)
                                        {
                                            propertiesToKeep.Add(property.Name, v2Element.Value);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return propertiesToKeep;
        }


    }
}
