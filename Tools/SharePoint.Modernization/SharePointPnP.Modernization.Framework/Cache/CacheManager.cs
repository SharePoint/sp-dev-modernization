﻿using Microsoft.SharePoint.Client;
using Newtonsoft.Json;
using OfficeDevPnP.Core.Pages;
using SharePointPnP.Modernization.Framework.Entities;
using SharePointPnP.Modernization.Framework.Publishing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SharePointPnP.Modernization.Framework.Cache
{
    /// <summary>
    /// Caching manager, singleton
    /// Important: don't cache SharePoint Client objects as these are tied to a specific client context and hence will fail when there's context switching!
    /// </summary>
    public sealed class CacheManager
    {
        private static readonly Lazy<CacheManager> _lazyInstance = new Lazy<CacheManager>(() => new CacheManager());
        private OfficeDevPnP.Core.Framework.Provisioning.Model.ProvisioningTemplate baseTemplate;
        private ConcurrentDictionary<string, List<ClientSideComponent>> clientSideComponents;
        private ConcurrentDictionary<Guid, string> siteToComponentMapping;
        private ConcurrentDictionary<string, List<FieldData>> fieldsToCopy;
        private ConcurrentDictionary<uint, string> publishingPagesLibraryNames;
        private ConcurrentDictionary<string, Dictionary<uint, string>> resourceStrings;
        private ConcurrentDictionary<string, PageLayout> generatedPageLayoutMappings;      
        private ConcurrentDictionary<string, Dictionary<int, UserEntity>> userJsonStrings;
        private ConcurrentDictionary<string, string> contentTypes;
        private ConcurrentDictionary<string, List<FieldData>> publishingContentTypeFields;

        /// <summary>
        /// Get's the single cachemanager instance, singleton pattern
        /// </summary>
        public static CacheManager Instance
        {
            get
            {
                return _lazyInstance.Value;
            }
        }

        #region Construction
        private CacheManager()
        {
            // place for instance initialization code
            clientSideComponents = new ConcurrentDictionary<string, List<ClientSideComponent>>(10, 10);
            siteToComponentMapping = new ConcurrentDictionary<Guid, string>(10, 100);
            baseTemplate = null;
            fieldsToCopy = new ConcurrentDictionary<string, List<FieldData>>(10, 10);
            AssetsTransfered = new List<AssetTransferredEntity>();
            publishingPagesLibraryNames = new ConcurrentDictionary<uint, string>(10, 10);
            resourceStrings = new ConcurrentDictionary<string, Dictionary<uint, string>>();
            generatedPageLayoutMappings = new ConcurrentDictionary<string, PageLayout>();
            userJsonStrings = new ConcurrentDictionary<string, Dictionary<int, UserEntity>>();
            contentTypes = new ConcurrentDictionary<string, string>();
            publishingContentTypeFields = new ConcurrentDictionary<string, List<FieldData>>();
        }
        #endregion

        #region Asset Transfer
        /// <summary>
        /// List of assets transferred from source to destination
        /// </summary>
        public List<AssetTransferredEntity> AssetsTransfered { get; set; }
        #endregion

        #region Client Side Components
        /// <summary>
        /// Get's the clientside components from cache or if needed retrieves and caches them
        /// </summary>
        /// <param name="page">Page to grab the components for</param>
        /// <returns></returns>
        public List<ClientSideComponent> GetClientSideComponents(ClientSidePage page)
        {
            Guid webId = page.Context.Web.EnsureProperty(o=>o.Id);

            if (siteToComponentMapping.ContainsKey(webId))
            {
                // Components are cached for this site, get the component key
                if (siteToComponentMapping.TryGetValue(webId, out string componentKey))
                {
                    if (clientSideComponents.TryGetValue(componentKey, out List<ClientSideComponent> componentList))
                    {
                        return componentList;
                    }
                }
            }

            // Ok, so nothing in cache so it seems, so let's get the components
            var componentsToAdd = page.AvailableClientSideComponents().ToList();

            // calculate the componentkey
            string componentKeyToCache = Sha256(JsonConvert.SerializeObject(componentsToAdd));

            // store the retrieved data in cache
            if (siteToComponentMapping.TryAdd(webId, componentKeyToCache))
            {
                // Since the components list is big and often the same across webs we only store it in cache if it's different
                if (!clientSideComponents.ContainsKey(componentKeyToCache))
                {
                    clientSideComponents.TryAdd(componentKeyToCache, componentsToAdd);
                }
            }

            return componentsToAdd;
        }

        /// <summary>
        /// Clear the clientside component cache
        /// </summary>
        public void ClearClientSideComponents()
        {
            clientSideComponents.Clear();
            siteToComponentMapping.Clear();
        }
        #endregion

        #region Base template and metadata
        /// <summary>
        /// Get's the base template that will be used to filter out "OOB" fields
        /// </summary>
        /// <param name="web">web to operate against</param>
        /// <returns>Provisioning template of the base template of STS#0</returns>
        public OfficeDevPnP.Core.Framework.Provisioning.Model.ProvisioningTemplate GetBaseTemplate(Web web)
        {
            if (this.baseTemplate == null)
            {
                this.baseTemplate = web.GetBaseTemplate("STS", 0);

                // Ensure certain new fields are there
                var sitePagesInBaseTemplate = baseTemplate.Lists.Where(p => p.Url == "SitePages").FirstOrDefault();
                if (sitePagesInBaseTemplate != null)
                {
                    AddFieldRef(sitePagesInBaseTemplate.FieldRefs, new Guid("ccc1037f-f65e-434a-868e-8c98af31fe29"), "_ComplianceFlags");
                    AddFieldRef(sitePagesInBaseTemplate.FieldRefs, new Guid("d4b6480a-4bed-4094-9a52-30181ea38f1d"), "_ComplianceTag");
                    AddFieldRef(sitePagesInBaseTemplate.FieldRefs, new Guid("92be610e-ddbb-49f4-b3b1-5c2bc768df8f"), "_ComplianceTagWrittenTime");
                    AddFieldRef(sitePagesInBaseTemplate.FieldRefs, new Guid("418d7676-2d6f-42cf-a16a-e43d2971252a"), "_ComplianceTagUserId");
                    AddFieldRef(sitePagesInBaseTemplate.FieldRefs, new Guid("8382d247-72a9-44b1-9794-7b177edc89f3"), "_IsRecord");
                    AddFieldRef(sitePagesInBaseTemplate.FieldRefs, new Guid("d307dff3-340f-44a2-9f4b-fbfe1ba07459"), "_CommentCount");
                    AddFieldRef(sitePagesInBaseTemplate.FieldRefs, new Guid("db8d9d6d-dc9a-4fbd-85f3-4a753bfdc58c"), "_LikeCount");
                    AddFieldRef(sitePagesInBaseTemplate.FieldRefs, new Guid("3a6b296c-3f50-445c-a13f-9c679ea9dda3"), "ComplianceAssetId");
                    AddFieldRef(sitePagesInBaseTemplate.FieldRefs, new Guid("9de685c5-fdf5-4319-b987-3edf55efb36f"), "_SPSitePageFlags");
                    AddFieldRef(sitePagesInBaseTemplate.FieldRefs, new Guid("1a53ab5a-11f9-4b92-a377-8cfaaf6ba7be"), "_DisplayName");
                }
            }

            return this.baseTemplate;
        }

        /// <summary>
        /// Clear base template cache
        /// </summary>
        public void ClearBaseTemplate()
        {
            this.baseTemplate = null;
        }

        /// <summary>
        /// Get the list of fields that need to be copied from cache. If cache is empty the list will be calculated
        /// </summary>
        /// <param name="web">Web to operate against</param>
        /// <param name="pagesLibrary">Pages library instance</param>
        /// <returns>List of fields that need to be copied</returns>
        public List<FieldData> GetFieldsToCopy(Web web, List pagesLibrary)
        {
            List<FieldData> fieldsToCopyRetrieved = new List<FieldData>();

            // Did we already do the calculation for this sitepages library? If so then return from cache
            if (fieldsToCopy.ContainsKey(pagesLibrary.Id.ToString()))
            {
                if (fieldsToCopy.TryGetValue(pagesLibrary.Id.ToString(), out List<FieldData> fields))
                {
                    return fields;
                }
            }
            else
            {
                // calculate the fields to copy
                var baseTemplate = GetBaseTemplate(web);
                if (baseTemplate != null)
                {
                    var sitePagesInBaseTemplate = baseTemplate.Lists.Where(p => p.Url == "SitePages").FirstOrDefault();

                    // Compare site pages list fields
                    foreach (var sitePagesField in pagesLibrary.Fields.Where(p => p.Hidden == false).ToList())
                    {
                        // Skip OOB fields
                        if (!OfficeDevPnP.Core.Enums.BuiltInFieldId.Contains(sitePagesField.Id))
                        {
                            if (sitePagesInBaseTemplate != null)
                            {
                                var fieldFoundInBaseSitePages = sitePagesInBaseTemplate.FieldRefs.Where(p => p.Name == sitePagesField.StaticName).FirstOrDefault();
                                if (fieldFoundInBaseSitePages == null)
                                {
                                    // copy metadata for this field
                                    FieldData fieldToAdd = new FieldData()
                                    {
                                        FieldName = sitePagesField.StaticName,
                                        FieldId = sitePagesField.Id,
                                        FieldType = sitePagesField.TypeAsString,
                                    };

                                    fieldsToCopyRetrieved.Add(fieldToAdd);
                                }
                            }
                        }
                    }

                    // Add to cache
                    if (fieldsToCopy.TryAdd(pagesLibrary.Id.ToString(), fieldsToCopyRetrieved))
                    {
                        return fieldsToCopyRetrieved;
                    }
                }
            }

            // We should not get here...
            return null;
        }

        public FieldData GetPublishingContentTypeField(List pagesLibrary, string contentTypeId, string fieldName)
        {
            // Try to get from cache
            if (this.publishingContentTypeFields.TryGetValue(contentTypeId, out List<FieldData> fieldsFromCache))
            {
                // return field if found
                return fieldsFromCache.Where(p => p.FieldName.Equals(fieldName, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
            }

            ClientContext context = pagesLibrary.Context as ClientContext;

            // Get the content type object
            var ctCol = pagesLibrary.ContentTypes;
            var ctType = context.LoadQuery(ctCol.Where(item => item.StringId == contentTypeId));
            context.ExecuteQueryRetry();

            if (ctType.FirstOrDefault() != null)
            {
                // Load all fields
                FieldCollection fields = ctType.FirstOrDefault().Fields;
                context.Load(fields, fs => fs.Include(f => f.Id, f => f.TypeAsString, f => f.InternalName));
                context.ExecuteQueryRetry();

                List<FieldData> contentTypeFields = new List<FieldData>();
                foreach (var field in fields)
                {
                    contentTypeFields.Add(new FieldData()
                    {
                        FieldId = field.Id,
                        FieldName = field.InternalName,
                        FieldType = field.TypeAsString,
                    });
                }

                // Store in cache
                this.publishingContentTypeFields.TryAdd(contentTypeId, contentTypeFields);
                
                // Return field, if found
                return contentTypeFields.Where(p => p.FieldName.Equals(fieldName, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
            }

            return null;
        }

        /// <summary>
        /// Clear the fields to copy cache
        /// </summary>
        public void ClearFieldsToCopy()
        {
            this.fieldsToCopy.Clear();
            this.publishingContentTypeFields.Clear();
        }

        #endregion

        #region Publishing Pages library name
        /// <summary>
        /// Get translation for the publishing pages library
        /// </summary>
        /// <param name="context">Context of the site</param>
        /// <returns>Translated name of the pages library</returns>
        public string GetPublishingPagesLibraryName(ClientContext context)
        {
            if (context == null)
            {
                return "pages";
            }

            uint lcid = context.Web.EnsureProperty(p => p.Language);

            if (publishingPagesLibraryNames.ContainsKey(lcid))
            {
                if (publishingPagesLibraryNames.TryGetValue(lcid, out string name))
                {
                    return name;
                }
                else
                {
                    // let's fallback to the default...we should never get here unless there's some threading issue
                    return "pages";
                }
            }
            else
            {
                ClientResult<string> result = Microsoft.SharePoint.Client.Utilities.Utility.GetLocalizedString(context, "$Resources:List_Pages_UrlName", "osrvcore", int.Parse(lcid.ToString()));
                context.ExecuteQueryRetry();

                string pagesLibraryName = new Regex(@"['´`]").Replace(result.Value, "");

                if (string.IsNullOrEmpty(pagesLibraryName))
                {
                    return "pages";
                }

                // add to cache
                publishingPagesLibraryNames.TryAdd(lcid, pagesLibraryName.ToLower());

                return pagesLibraryName.ToLower();
            }
        }
        #endregion

        #region Resource strings
        /// <summary>
        /// Returns the translated value for a resource string
        /// </summary>
        /// <param name="context">Context of the site</param>
        /// <param name="resource">Key of the resource (e.g. $Resources:core,ScriptEditorWebPartDescription;) </param>
        /// <returns>Translated string</returns>
        public string GetResourceString(ClientContext context, string resource)
        {
            uint lcid = context.Web.EnsureProperty(p => p.Language);

            if (resourceStrings.ContainsKey(resource))
            {
                if (resourceStrings.TryGetValue(resource, out Dictionary<uint, string> resourceValues))
                {
                    if (resourceValues.ContainsKey(lcid))
                    {
                        if (resourceValues.TryGetValue(lcid, out string resourceValue))
                        {
                            return resourceValue;
                        }
                    }
                }
            }

            // If we got here then we need to still add the resource translation
            var resourceString = resource.Replace("$Resources:", "").Replace(";", "");
            var splitResourceString = resourceString.Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries);
            string resourceFile = "core";
            string resourceKey = null;
            if (splitResourceString.Length == 2)
            {
                resourceFile = splitResourceString[0];
                resourceKey = splitResourceString[1];
            }
            else
            {
                resourceKey = splitResourceString[0];
            }

            ClientResult<string> result = Microsoft.SharePoint.Client.Utilities.Utility.GetLocalizedString(context, $"$Resources:{resourceKey}", resourceFile, int.Parse(lcid.ToString()));
            context.ExecuteQueryRetry();

            if (result == null)
            {
                return resource;
            }

            if (resourceStrings.ContainsKey(resource))
            {
                if (resourceStrings.TryGetValue(resource, out Dictionary<uint, string> resourceValues))
                {
                    if (!resourceValues.ContainsKey(lcid))
                    {
                        // Add translations in existing array
                        Dictionary<uint, string> newResourceValues = new Dictionary<uint, string>(resourceValues)
                        {
                            { lcid, result.Value }
                        };
                        resourceStrings.TryUpdate(resource, newResourceValues, resourceValues);
                    }
                }
            }
            else
            {
                // No translations were already retrieved in this language
                Dictionary<uint, string> translations = new Dictionary<uint, string>
                {
                    { lcid, result.Value }
                };

                resourceStrings.TryAdd(resource, translations);
            }

            return result.Value;
        }
        #endregion

        #region PageLayout mappings
        public PageLayout GetPageLayoutMapping(ListItem page)
        {
            //string key = page[Constants.FileRefField].ToString();
            string key = page.PageLayoutFile();

            // Try get the page layout from cache
            if (generatedPageLayoutMappings.TryGetValue(key, out PageLayout pageLayoutFromCache))
            {
                return pageLayoutFromCache;
            }

            PageLayoutAnalyser pageLayoutAnalyzer = new PageLayoutAnalyser(page.Context as ClientContext);

            // Let's try to generate a 'basic' model and use that...not optimal, but better than bailing out.
            var newPageLayoutMapping = pageLayoutAnalyzer.AnalysePageLayoutFromPublishingPage(page);

            // Add to cache for future reuse
            generatedPageLayoutMappings.TryAdd(key, newPageLayoutMapping);

            // Return to requestor
            return newPageLayoutMapping;
        }

        #endregion

        #region Generic methods
        public void ClearAllCaches()
        {
            this.AssetsTransfered.Clear();
            ClearClientSideComponents();
            ClearBaseTemplate();
            ClearFieldsToCopy();            
        }
        #endregion

        #region Users
        public UserEntity GetUserFromUserList(ClientContext context, int userListId)
        {
            string key = context.Web.EnsureProperty(p => p.Url);

            if (this.userJsonStrings.TryGetValue(key, out Dictionary<int, UserEntity> userListFromCache))
            {
                if (userListFromCache.TryGetValue(userListId, out UserEntity userJsonFromCache))
                {
                    return userJsonFromCache;
                }
            }

            try
            {
                string CAMLQueryByName = @"
                <View Scope='Recursive'>
                  <Query>
                    <Where>
                      <Contains>
                        <FieldRef Name='ID'/>
                        <Value Type='Integer'>{0}</Value>
                      </Contains>
                    </Where>
                  </Query>
                </View>";

                List siteUserInfoList = context.Web.SiteUserInfoList;
                CamlQuery query = new CamlQuery
                {
                    ViewXml = String.Format(CAMLQueryByName, userListId)
                };
                var loadedUsers = context.LoadQuery(siteUserInfoList.GetItems(query));
                context.ExecuteQueryRetry();

                UserEntity author = null;
                if (loadedUsers != null)
                {
                    var loadedUser = loadedUsers.FirstOrDefault();
                    if (loadedUser != null)
                    {
                        // Does not work for groups
                        if (loadedUser["UserName"] == null)
                        {
                            return null;
                        }

                        author = new UserEntity()
                        {
                            Upn = loadedUser["UserName"].ToString(),
                            Name = loadedUser["Title"] != null ? loadedUser["Title"].ToString() : "",
                            Role = loadedUser["JobTitle"] != null ? loadedUser["JobTitle"].ToString() : "",
                        };

                        author.Id = $"i:0#.f|membership|{author.Upn}";

                        // Store in cache
                        if (userListFromCache != null)
                        {
                            // We already has a user list, simply add this one
                            Dictionary<int, UserEntity> newUserListToCache = new Dictionary<int, UserEntity>(userListFromCache)
                            {
                                { userListId, author }
                            };

                            this.userJsonStrings.TryUpdate(key, newUserListToCache, userListFromCache);
                        }
                        else
                        {
                            // First user for this key (= web)
                            Dictionary<int, UserEntity> newUserListToCache = new Dictionary<int, UserEntity>()
                            {
                                { userListId, author }
                            };

                            this.userJsonStrings.TryAdd(key, newUserListToCache);
                        }

                        // return 
                        return author;
                    }
                }
            }
            catch (Exception ex)
            {
                // TODO logging
            }

            return null;
        }
        #endregion

        #region Content types
        public string GetContentTypeId(List pagesLibrary, string contentTypeName)
        {
            string contentTypeId = null;

            // try to get from cache
            this.contentTypes.TryGetValue(contentTypeName, out string contentTypeIdFromCache);
            if (!string.IsNullOrEmpty(contentTypeIdFromCache))
            {
                return contentTypeIdFromCache;
            }

            // Load content type
            var ctCol = pagesLibrary.ContentTypes;
            var results = pagesLibrary.Context.LoadQuery(ctCol.Where(item => item.Name == contentTypeName));
            pagesLibrary.Context.ExecuteQueryRetry();

            if (results.FirstOrDefault() != null)
            {
                contentTypeId = results.FirstOrDefault().StringId;
                
                // We only allow content types that inherit from the OOB Site Page content type
                if (!contentTypeId.StartsWith(Constants.ModernPageContentTypeId, StringComparison.InvariantCultureIgnoreCase))
                {
                    return null;
                }

                // add to cache
                this.contentTypes.TryAdd(contentTypeName, contentTypeId);
            }

            return contentTypeId;
        }
        #endregion

        #region Helper methods
        private static string Sha256(string randomString)
        {
            var crypt = new System.Security.Cryptography.SHA256Managed();
            var hash = new StringBuilder();
            byte[] crypto = crypt.ComputeHash(Encoding.UTF8.GetBytes(randomString));
            foreach (byte theByte in crypto)
            {
                hash.Append(theByte.ToString("x2"));
            }
            return hash.ToString();
        }

        private void AddFieldRef(OfficeDevPnP.Core.Framework.Provisioning.Model.FieldRefCollection fieldRefs, Guid Id, string name)
        {
            if (fieldRefs.Where(p => p.Id.Equals(Id)).FirstOrDefault() == null)
            {
                fieldRefs.Add(new OfficeDevPnP.Core.Framework.Provisioning.Model.FieldRef(name) { Id = Id });
            }
        }
        #endregion
    }
}
 