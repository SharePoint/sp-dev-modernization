﻿using AngleSharp.Parser.Html;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Taxonomy;
using OfficeDevPnP.Core.Pages;
using SharePointPnP.Modernization.Framework.Cache;
using SharePointPnP.Modernization.Framework.Entities;
using SharePointPnP.Modernization.Framework.Extensions;
using SharePointPnP.Modernization.Framework.Telemetry;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace SharePointPnP.Modernization.Framework.Transform
{
    /// <summary>
    /// Base page transformator class that contains logic that applies for all page transformations
    /// </summary>
    public abstract class BasePageTransformator : BaseTransform
    {
        internal ClientContext sourceClientContext;
        internal ClientContext targetClientContext;
        internal Stopwatch watch;
        internal const string ExecutionLog = "execution.csv";
        internal PageTransformation pageTransformation;
        internal string version = "undefined";
        internal PageTelemetry pageTelemetry;
        internal bool isRootPage = false;
        // source page information to "restore"
        internal FieldUserValue SourcePageAuthor;
        internal FieldUserValue SourcePageEditor;
        internal DateTime SourcePageCreated;
        internal DateTime SourcePageModified;

        #region Helper methods
        internal string GetFieldValue(BaseTransformationInformation baseTransformationInformation, string fieldName)
        {

            if (baseTransformationInformation.SourcePage != null)
            {
                return baseTransformationInformation.SourcePage[fieldName].ToString();
            }
            else
            {

                if (baseTransformationInformation.SourceFile != null)
                {
                    var fileServerRelativeUrl = baseTransformationInformation.SourceFile.EnsureProperty(p => p.ServerRelativeUrl);

                    // come up with equivalent field values for the page without listitem (so page living in the root folder of the site)
                    if (fieldName.Equals(Constants.FileRefField))
                    {
                        // e.g. /sites/espctest2/SitePages/demo16.aspx
                        return fileServerRelativeUrl;
                    }
                    else if (fieldName.Equals(Constants.FileDirRefField))
                    {
                        // e.g. /sites/espctest2/SitePages
                        return fileServerRelativeUrl.Replace($"/{System.IO.Path.GetFileName(fileServerRelativeUrl)}", "");

                    }
                    else if (fieldName.Equals(Constants.FileLeafRefField))
                    {
                        // e.g. demo16.aspx
                        return System.IO.Path.GetFileName(fileServerRelativeUrl);
                    }
                }
                return "";
            }
        }

        internal bool FieldExistsAndIsUsed(BaseTransformationInformation baseTransformationInformation, string fieldName)
        {
            if (baseTransformationInformation.SourcePage != null)
            {
                return baseTransformationInformation.SourcePage.FieldExistsAndUsed(fieldName);
            }
            else
            {
                return true;
            }
        }

        internal bool IsRootPage(File file)
        {
            if (file != null)
            {
                return true;
            }

            return false;
        }

        internal void RemoveEmptyTextParts(ClientSidePage targetPage)
        {
            var textParts = targetPage.Controls.Where(p => p.Type == typeof(OfficeDevPnP.Core.Pages.ClientSideText));
            if (textParts != null && textParts.Any())
            {
                HtmlParser parser = new HtmlParser(new HtmlParserOptions() { IsEmbedded = true });

                foreach (var textPart in textParts.ToList())
                {
                    using (var document = parser.Parse(((OfficeDevPnP.Core.Pages.ClientSideText)textPart).Text))
                    {
                        if (document.FirstChild != null && string.IsNullOrEmpty(document.FirstChild.TextContent))
                        {
                            LogInfo(LogStrings.TransformRemovingEmptyWebPart, LogStrings.Heading_RemoveEmptyTextParts);
                            // Drop text part
                            targetPage.Controls.Remove(textPart);
                        }
                    }
                }
            }
        }

        internal void RemoveEmptySectionsAndColumns(ClientSidePage targetPage)
        {
            foreach (var section in targetPage.Sections.ToList())
            {
                // First remove all empty sections
                if (section.Controls.Count == 0)
                {
                    targetPage.Sections.Remove(section);
                }
            }

            // Remove empty columns
            foreach (var section in targetPage.Sections)
            {
                if (section.Type == CanvasSectionTemplate.TwoColumn ||
                    section.Type == CanvasSectionTemplate.TwoColumnLeft ||
                    section.Type == CanvasSectionTemplate.TwoColumnRight)
                {
                    var emptyColumn = section.Columns.Where(p => p.Controls.Count == 0).FirstOrDefault();
                    if (emptyColumn != null)
                    {
                        // drop the empty column and change to single column section
                        section.Columns.Remove(emptyColumn);
                        section.Type = CanvasSectionTemplate.OneColumn;
                        section.Columns.First().ResetColumn(0, 12);
                    }
                }
                else if (section.Type == CanvasSectionTemplate.ThreeColumn)
                {
                    var emptyColumns = section.Columns.Where(p => p.Controls.Count == 0);
                    if (emptyColumns != null)
                    {
                        if (emptyColumns.Any() && emptyColumns.Count() == 2)
                        {
                            // drop the two empty columns and change to single column section
                            foreach (var emptyColumn in emptyColumns.ToList())
                            {
                                section.Columns.Remove(emptyColumn);
                            }
                            section.Type = CanvasSectionTemplate.OneColumn;
                            section.Columns.First().ResetColumn(0, 12);
                        }
                        else if (emptyColumns.Any() && emptyColumns.Count() == 1)
                        {
                            // Remove the empty column and change to two column section
                            section.Columns.Remove(emptyColumns.First());
                            section.Type = CanvasSectionTemplate.TwoColumn;
                            int i = 0;
                            foreach (var column in section.Columns)
                            {
                                column.ResetColumn(i, 6);
                                i++;
                            }
                        }
                    }
                }
            }
        }

        internal void ApplyItemLevelPermissions(bool hasTargetContext, ListItem item, ListItemPermission lip, bool alwaysBreakItemLevelPermissions = false)
        {
            if (lip == null || item == null)
            {
                return;
            }

            // Break permission inheritance on the item if not done yet
            if (alwaysBreakItemLevelPermissions || !item.HasUniqueRoleAssignments)
            {
                item.BreakRoleInheritance(false, false);
                item.Context.ExecuteQueryRetry();
            }

            if (hasTargetContext)
            {
                // Ensure principals are available in the target site
                Dictionary<string, Principal> targetPrincipals = new Dictionary<string, Principal>(lip.Principals.Count);

                foreach (var principal in lip.Principals)
                {
                    var targetPrincipal = GetPrincipal(this.targetClientContext.Web, principal.Key, hasTargetContext);
                    if (targetPrincipal != null)
                    {
                        if (!targetPrincipals.ContainsKey(principal.Key))
                        {
                            targetPrincipals.Add(principal.Key, targetPrincipal);
                        }
                    }
                }

                // Assign item level permissions          
                foreach (var roleAssignment in lip.RoleAssignments)
                {
                    if (targetPrincipals.TryGetValue(roleAssignment.Member.LoginName, out Principal principal))
                    {
                        var roleDefinitionBindingCollection = new RoleDefinitionBindingCollection(this.targetClientContext);
                        foreach (var roleDef in roleAssignment.RoleDefinitionBindings)
                        {
                            var targetRoleDef = this.targetClientContext.Web.RoleDefinitions.GetByName(roleDef.Name);
                            if (targetRoleDef != null)
                            {
                                roleDefinitionBindingCollection.Add(targetRoleDef);
                            }
                        }
                        item.RoleAssignments.Add(principal, roleDefinitionBindingCollection);
                    }
                }

                this.targetClientContext.ExecuteQueryRetry();
            }
            else
            {
                // Assign item level permissions
                foreach (var roleAssignment in lip.RoleAssignments)
                {
                    if (lip.Principals.TryGetValue(roleAssignment.Member.LoginName, out Principal principal))
                    {
                        var roleDefinitionBindingCollection = new RoleDefinitionBindingCollection(this.sourceClientContext);
                        foreach (var roleDef in roleAssignment.RoleDefinitionBindings)
                        {
                            roleDefinitionBindingCollection.Add(roleDef);
                        }

                        item.RoleAssignments.Add(principal, roleDefinitionBindingCollection);
                    }
                }

                this.sourceClientContext.ExecuteQueryRetry();
            }

            LogInfo(LogStrings.TransformCopiedItemPermissions, LogStrings.Heading_ApplyItemLevelPermissions);
        }

        internal ListItemPermission GetItemLevelPermissions(bool hasTargetContext, List pagesLibrary, ListItem source, ListItem target)
        {
            ListItemPermission lip = null;

            if (source.IsPropertyAvailable("HasUniqueRoleAssignments") && source.HasUniqueRoleAssignments)
            {
                // You need to have the ManagePermissions permission before item level permissions can be copied
                if (pagesLibrary.EffectiveBasePermissions.Has(PermissionKind.ManagePermissions))
                {
                    // Copy the unique permissions from source to target
                    // Get the unique permissions
                    this.sourceClientContext.Load(source, a => a.EffectiveBasePermissions, a => a.RoleAssignments.Include(roleAsg => roleAsg.Member.LoginName,
                        roleAsg => roleAsg.RoleDefinitionBindings.Include(roleDef => roleDef.Name, roleDef => roleDef.Description)));
                    this.sourceClientContext.ExecuteQueryRetry();

                    if (source.EffectiveBasePermissions.Has(PermissionKind.ManagePermissions))
                    {
                        // Load the site groups
                        this.sourceClientContext.Load(this.sourceClientContext.Web.SiteGroups, p => p.Include(g => g.LoginName));

                        // Get target page information
                        if (hasTargetContext)
                        {
                            this.targetClientContext.Load(target, p => p.HasUniqueRoleAssignments, p => p.RoleAssignments);
                            this.targetClientContext.Load(this.targetClientContext.Web, p => p.RoleDefinitions);
                            this.targetClientContext.Load(this.targetClientContext.Web.SiteGroups, p => p.Include(g => g.LoginName));
                        }
                        else
                        {
                            this.sourceClientContext.Load(target, p => p.HasUniqueRoleAssignments, p => p.RoleAssignments);
                        }

                        this.sourceClientContext.ExecuteQueryRetry();

                        if (hasTargetContext)
                        {
                            this.targetClientContext.ExecuteQueryRetry();
                        }

                        Dictionary<string, Principal> principals = new Dictionary<string, Principal>(10);
                        lip = new ListItemPermission()
                        {
                            RoleAssignments = source.RoleAssignments,
                            Principals = principals
                        };

                        // Apply new permissions
                        foreach (var roleAssignment in source.RoleAssignments)
                        {
                            var principal = GetPrincipal(this.sourceClientContext.Web, roleAssignment.Member.LoginName);
                            if (principal != null)
                            {
                                if (!lip.Principals.ContainsKey(roleAssignment.Member.LoginName))
                                {
                                    lip.Principals.Add(roleAssignment.Member.LoginName, principal);
                                }
                            }
                        }
                    }
                }
            }

            LogInfo(LogStrings.TransformGetItemPermissions, LogStrings.Heading_ApplyItemLevelPermissions);

            return lip;
        }

        internal Principal GetPrincipal(Web web, string principalInput, bool hasTargetContext = false)
        {
            Principal principal = web.SiteGroups.FirstOrDefault(g => g.LoginName.Equals(principalInput, StringComparison.OrdinalIgnoreCase));

            if (principal == null)
            {
                if (principalInput.Contains("#ext#"))
                {
                    principal = web.SiteUsers.FirstOrDefault(u => u.LoginName.Equals(principalInput));

                    if (principal == null)
                    {
                        //Skipping external user...
                    }
                }
                else
                {
                    try
                    {
                        principal = web.EnsureUser(principalInput);
                        web.Context.ExecuteQueryRetry();
                    }
                    catch (Exception ex)
                    {
                        if (!hasTargetContext)
                        {
                            //Failed to EnsureUser, we're not failing for this, only log as error when doing an in site transformation as it's not expected to fail here
                            LogError(LogStrings.Error_GetPrincipalFailedEnsureUser, LogStrings.Heading_GetPrincipal, ex);
                        }

                        principal = null;
                    }
                }
            }

            return principal;
        }

        internal void CopyPageMetadata(PageTransformationInformation pageTransformationInformation, string pageType, ClientSidePage targetPage, List targetPagesLibrary)
        {
            var fieldsToCopy = CacheManager.Instance.GetFieldsToCopy(this.sourceClientContext.Web, targetPagesLibrary, pageType);
            bool listItemWasReloaded = false;
            if (fieldsToCopy.Count > 0)
            {
                // Load the target page list item
                targetPage.Context.Load(targetPage.PageListItem);
                targetPage.Context.ExecuteQueryRetry();

                pageTransformationInformation.SourcePage.EnsureProperty(p => p.ContentType);

                // regular fields
                bool isDirty = false;

                var sitePagesServerRelativeUrl = OfficeDevPnP.Core.Utilities.UrlUtility.Combine(targetPage.Context.Web.ServerRelativeUrl.TrimEnd(new char[] { '/' }), "sitepages");
                List targetSitePagesLibrary = targetPage.Context.Web.GetList(sitePagesServerRelativeUrl);
                targetPage.Context.Load(targetSitePagesLibrary, l => l.Fields.IncludeWithDefaultProperties(f => f.Id, f => f.Title, f => f.Hidden, f => f.InternalName, f => f.DefaultValue, f => f.Required, f => f.StaticName));
                targetPage.Context.ExecuteQueryRetry();

                string contentTypeId = CacheManager.Instance.GetContentTypeId(targetPage.PageListItem.ParentList, pageTransformationInformation.SourcePage.ContentType.Name);
                if (!string.IsNullOrEmpty(contentTypeId))
                {
                    // Load the target page list item, needs to be loaded as it was previously saved and we need to avoid version conflicts
                    targetPage.Context.Load(targetPage.PageListItem);
                    targetPage.Context.ExecuteQueryRetry();
                    listItemWasReloaded = true;

                    targetPage.PageListItem[Constants.ContentTypeIdField] = contentTypeId;
                    targetPage.PageListItem.UpdateOverwriteVersion();
                    isDirty = true;
                }

                // taxonomy fields
                foreach (var fieldToCopy in fieldsToCopy.Where(p => p.FieldType == "TaxonomyFieldTypeMulti" || p.FieldType == "TaxonomyFieldType"))
                {
                    if (!listItemWasReloaded)
                    {
                        // Load the target page list item, needs to be loaded as it was previously saved and we need to avoid version conflicts
                        targetPage.Context.Load(targetPage.PageListItem);
                        targetPage.Context.ExecuteQueryRetry();
                        listItemWasReloaded = true;
                    }
                    switch (fieldToCopy.FieldType)
                    {
                        case "TaxonomyFieldTypeMulti":
                            {
                                var taxFieldBeforeCast = targetSitePagesLibrary.Fields.Where(p => p.StaticName.Equals(fieldToCopy.FieldName)).FirstOrDefault();
                                if (taxFieldBeforeCast != null)
                                {
                                    var taxField = targetPage.Context.CastTo<TaxonomyField>(taxFieldBeforeCast);

                                    if (pageTransformationInformation.SourcePage[fieldToCopy.FieldName] != null)
                                    {
                                        if (pageTransformationInformation.SourcePage[fieldToCopy.FieldName] is TaxonomyFieldValueCollection)
                                        {
                                            var valueCollectionToCopy = (pageTransformationInformation.SourcePage[fieldToCopy.FieldName] as TaxonomyFieldValueCollection);
                                            var taxonomyFieldValueArray = valueCollectionToCopy.Select(taxonomyFieldValue => $"-1;#{taxonomyFieldValue.Label}|{taxonomyFieldValue.TermGuid}");
                                            var valueCollection = new TaxonomyFieldValueCollection(targetPage.Context, string.Join(";#", taxonomyFieldValueArray), taxField);
                                            taxField.SetFieldValueByValueCollection(targetPage.PageListItem, valueCollection);
                                        }
                                        else if (pageTransformationInformation.SourcePage[fieldToCopy.FieldName] is Dictionary<string, object>)
                                        {
                                            var taxDictionaryList = (pageTransformationInformation.SourcePage[fieldToCopy.FieldName] as Dictionary<string, object>);
                                            var valueCollectionToCopy = taxDictionaryList["_Child_Items_"] as Object[];

                                            List<string> taxonomyFieldValueArray = new List<string>();
                                            for (int i = 0; i < valueCollectionToCopy.Length; i++)
                                            {
                                                var taxDictionary = valueCollectionToCopy[i] as Dictionary<string, object>;
                                                taxonomyFieldValueArray.Add($"-1;#{taxDictionary["Label"].ToString()}|{taxDictionary["TermGuid"].ToString()}");
                                            }
                                            var valueCollection = new TaxonomyFieldValueCollection(targetPage.Context, string.Join(";#", taxonomyFieldValueArray), taxField);
                                            taxField.SetFieldValueByValueCollection(targetPage.PageListItem, valueCollection);
                                        }

                                        isDirty = true;
                                        LogInfo($"{LogStrings.TransformCopyingMetaDataField} {fieldToCopy.FieldName}", LogStrings.Heading_CopyingPageMetadata);
                                    }
                                }
                                else
                                {
                                    LogWarning($"{LogStrings.TransformCopyingMetaDataFieldSkipped} {fieldToCopy.FieldName}", LogStrings.Heading_CopyingPageMetadata);
                                    break;
                                }
                                break;
                            }
                        case "TaxonomyFieldType":
                            {
                                var taxFieldBeforeCast = targetSitePagesLibrary.Fields.Where(p => p.StaticName.Equals(fieldToCopy.FieldName)).FirstOrDefault();
                                if (taxFieldBeforeCast != null)
                                {
                                    var taxField = targetPage.Context.CastTo<TaxonomyField>(taxFieldBeforeCast);
                                    var taxValue = new TaxonomyFieldValue();
                                    if (pageTransformationInformation.SourcePage[fieldToCopy.FieldName] != null)
                                    {
                                        if (pageTransformationInformation.SourcePage[fieldToCopy.FieldName] is TaxonomyFieldValue)
                                        {
                                            taxValue.Label = (pageTransformationInformation.SourcePage[fieldToCopy.FieldName] as TaxonomyFieldValue).Label;
                                            taxValue.TermGuid = (pageTransformationInformation.SourcePage[fieldToCopy.FieldName] as TaxonomyFieldValue).TermGuid;
                                            taxValue.WssId = -1;
                                        }
                                        else if (pageTransformationInformation.SourcePage[fieldToCopy.FieldName] is Dictionary<string, object>)
                                        {
                                            var taxDictionary = (pageTransformationInformation.SourcePage[fieldToCopy.FieldName] as Dictionary<string, object>);
                                            taxValue.Label = taxDictionary["Label"].ToString();
                                            taxValue.TermGuid = taxDictionary["TermGuid"].ToString();
                                            taxValue.WssId = -1;
                                        }
                                        taxField.SetFieldValueByValue(targetPage.PageListItem, taxValue);
                                        isDirty = true;
                                        LogInfo($"{LogStrings.TransformCopyingMetaDataField} {fieldToCopy.FieldName}", LogStrings.Heading_CopyingPageMetadata);
                                    }
                                }
                                else
                                {
                                    LogWarning($"{LogStrings.TransformCopyingMetaDataFieldSkipped} {fieldToCopy.FieldName}", LogStrings.Heading_CopyingPageMetadata);
                                    break;
                                }
                                break;
                            }
                    }
                }

                if (isDirty)
                {
                    targetPage.PageListItem.UpdateOverwriteVersion();
                    targetPage.Context.Load(targetPage.PageListItem);
                    targetPage.Context.ExecuteQueryRetry();
                    isDirty = false;
                }

                foreach (var fieldToCopy in fieldsToCopy.Where(p => p.FieldType != "TaxonomyFieldTypeMulti" && p.FieldType != "TaxonomyFieldType"))
                {
                    var targetField = targetSitePagesLibrary.Fields.Where(p => p.StaticName.Equals(fieldToCopy.FieldName)).FirstOrDefault();

                    if (targetField != null && pageTransformationInformation.SourcePage[fieldToCopy.FieldName] != null)
                    {
                        if (fieldToCopy.FieldType == "User" || fieldToCopy.FieldType == "UserMulti")
                        {
                            if (pageTransformationInformation.IsCrossFarmTransformation)
                            {
                                // we can't copy these fields in a cross farm scenario as we do not yet support user account mapping
                                LogWarning($"{LogStrings.TransformCopyingUserMetaDataFieldSkipped} {fieldToCopy.FieldName}", LogStrings.Heading_CopyingPageMetadata);
                            }
                            else
                            {
                                object fieldValueToSet = pageTransformationInformation.SourcePage[fieldToCopy.FieldName];
                                if (fieldValueToSet is FieldUserValue)
                                {
                                    using (var clonedTargetContext = targetPage.Context.Clone(targetPage.Context.Web.GetUrl()))
                                    {
                                        try
                                        {
                                            var user = clonedTargetContext.Web.EnsureUser((fieldValueToSet as FieldUserValue).LookupValue);
                                            clonedTargetContext.Load(user);
                                            clonedTargetContext.ExecuteQueryRetry();

                                            // Prep a new FieldUserValue object instance and update the list item
                                            var newUser = new FieldUserValue()
                                            {
                                                LookupId = user.Id
                                            };
                                            targetPage.PageListItem[fieldToCopy.FieldName] = newUser;
                                        }
                                        catch (Exception ex)
                                        {
                                            LogWarning(string.Format(LogStrings.Warning_UserIsNotResolving, (fieldValueToSet as FieldUserValue).LookupValue, ex.Message), LogStrings.Heading_CopyingPageMetadata);
                                        }
                                    }
                                }
                                else
                                {
                                    List<FieldUserValue> userValues = new List<FieldUserValue>();
                                    using (var clonedTargetContext = targetPage.Context.Clone(targetPage.Context.Web.GetUrl()))
                                    {
                                        foreach (var currentUser in (fieldValueToSet as Array))
                                        {
                                            try
                                            {
                                                var user = clonedTargetContext.Web.EnsureUser((currentUser as FieldUserValue).LookupValue);
                                                clonedTargetContext.Load(user);
                                                clonedTargetContext.ExecuteQueryRetry();

                                                // Prep a new FieldUserValue object instance
                                                var newUser = new FieldUserValue()
                                                {
                                                    LookupId = user.Id
                                                };

                                                userValues.Add(newUser);
                                            }
                                            catch (Exception ex)
                                            {
                                                LogWarning(string.Format(LogStrings.Warning_UserIsNotResolving, (fieldValueToSet as FieldUserValue).LookupValue, ex.Message), LogStrings.Heading_CopyingPageMetadata);
                                            }
                                        }

                                        if (userValues.Count > 0)
                                        {
                                            targetPage.PageListItem[fieldToCopy.FieldName] = userValues.ToArray();
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Handling of "special" fields

                            // PostCategory is a default field on a blog post, but it's a lookup. Let's copy as regular field
                            if (fieldToCopy.FieldId.Equals(Constants.PostCategory))
                            {
                                string postCategoryFieldValue = null;
                                if (((FieldLookupValue[])pageTransformationInformation.SourcePage[fieldToCopy.FieldName]).Length > 1)
                                {
                                    postCategoryFieldValue += ";#";
                                    foreach (var fieldLookupValue in (FieldLookupValue[])pageTransformationInformation.SourcePage[fieldToCopy.FieldName])
                                    {
                                        postCategoryFieldValue = postCategoryFieldValue + fieldLookupValue.LookupValue + ";#";
                                    }
                                }
                                else
                                {
                                    postCategoryFieldValue = ((FieldLookupValue[])pageTransformationInformation.SourcePage[fieldToCopy.FieldName])[0].LookupValue;
                                }

                                targetPage.PageListItem[fieldToCopy.FieldName] = postCategoryFieldValue;
                            }
                            // Regular field handling
                            else
                            {
                                targetPage.PageListItem[fieldToCopy.FieldName] = pageTransformationInformation.SourcePage[fieldToCopy.FieldName];
                            }
                        }

                        isDirty = true;
                        LogInfo($"{LogStrings.TransformCopyingMetaDataField} {fieldToCopy.FieldName}", LogStrings.Heading_CopyingPageMetadata);
                    }
                    else
                    {
                        LogWarning($"{LogStrings.TransformCopyingMetaDataFieldSkipped} {fieldToCopy.FieldName}", LogStrings.Heading_CopyingPageMetadata);
                    }
                }

                if (isDirty)
                {
                    targetPage.PageListItem.UpdateOverwriteVersion();
                    targetPage.Context.Load(targetPage.PageListItem);
                    targetPage.Context.ExecuteQueryRetry();
                    isDirty = false;
                }
            }
        }

        /// <summary>
        /// Gets the version of the assembly
        /// </summary>
        /// <returns></returns>
        internal string GetVersion()
        {
            try
            {
                var coreAssembly = Assembly.GetExecutingAssembly();
                return ((AssemblyFileVersionAttribute)coreAssembly.GetCustomAttribute(typeof(AssemblyFileVersionAttribute))).Version.ToString();
            }
            catch (Exception ex)
            {
                LogError(LogStrings.Error_GetVersionError, LogStrings.Heading_GetVersion, ex, true);
            }

            return "undefined";
        }

        internal void InitMeasurement()
        {
            try
            {
                if (System.IO.File.Exists(ExecutionLog))
                {
                    System.IO.File.Delete(ExecutionLog);
                }
            }
            catch { }
        }

        internal void Start()
        {
            watch = Stopwatch.StartNew();
        }

        internal void Stop(string method)
        {
            watch.Stop();
            var elapsedTime = watch.ElapsedMilliseconds;
            System.IO.File.AppendAllText(ExecutionLog, $"{method};{elapsedTime}{Environment.NewLine}");
        }

        /// <summary>
        /// Loads the telemetry and properties for the client object
        /// </summary>
        /// <param name="clientContext"></param>
        internal void LoadClientObject(ClientContext clientContext, bool isTargetContext)
        {
            if (clientContext != null)
            {
                clientContext.ClientTag = $"SPDev:PageTransformator";
                // Load all web properties needed further one
                clientContext.Web.GetUrl();
                if (isTargetContext)
                {
                    clientContext.Load(clientContext.Web, p => p.Id, p => p.ServerRelativeUrl, p => p.RootFolder.WelcomePage, p => p.Language, p => p.WebTemplate);
                }
                else
                {
                    clientContext.Load(clientContext.Web, p => p.Id, p => p.ServerRelativeUrl, p => p.RootFolder.WelcomePage, p => p.Language);
                }
                clientContext.Load(clientContext.Site, p => p.RootWeb.ServerRelativeUrl, p => p.Id, p => p.Url);
                // Use regular ExecuteQuery as we want to send this custom clienttag
                clientContext.ExecuteQuery();
            }
        }

        internal void PopulateGlobalProperties(ClientContext sourceContext, ClientContext targetContext)
        {
            // Azure AD Tenant ID
            if (targetContext != null)
            {
                // Cache tenant id
                this.pageTelemetry.LoadAADTenantId(targetContext);
            }
            else
            {
                // Cache tenant id
                this.pageTelemetry.LoadAADTenantId(sourceContext);
            }
        }

        /// <summary>
        /// Validates settings when doing a cross farm transformation
        /// </summary>
        /// <param name="baseTransformationInformation">Transformation Information</param>
        /// <remarks>Will disable feature if not supported</remarks>
        internal void CrossFarmTransformationValidation(BaseTransformationInformation baseTransformationInformation)
        {
            // Source only context - allow item level permissions
            // Source to target same base address - allow item level permissions
            // Source to target difference base address - disallow item level permissions

            if (targetClientContext != null && sourceClientContext != null)
            {
                if (!sourceClientContext.Url.Equals(targetClientContext.Url, StringComparison.InvariantCultureIgnoreCase))
                {
                    baseTransformationInformation.IsCrossSiteTransformation = true;
                }

                var sourceUrl = sourceClientContext.Url.GetBaseUrl();
                var targetUrl = targetClientContext.Url.GetBaseUrl();

                // Override the setting for keeping item level permissions
                if (!sourceUrl.Equals(targetUrl, StringComparison.InvariantCultureIgnoreCase))
                {
                    baseTransformationInformation.KeepPageSpecificPermissions = false;
                    LogWarning(LogStrings.Warning_ContextValidationFailWithKeepPermissionsEnabled, LogStrings.Heading_InputValidation);

                    // Set a global flag to indicate this is a cross farm transformation (on-prem to SPO tenant or SPO Tenant A to SPO Tenant B)
                    baseTransformationInformation.IsCrossFarmTransformation = true;
                }
            }

            if (sourceClientContext != null)
            {
                baseTransformationInformation.SourceVersion = GetVersion(sourceClientContext);
                baseTransformationInformation.SourceVersionNumber = GetExactVersion(sourceClientContext);
            }

            if (targetClientContext != null)
            {
                baseTransformationInformation.TargetVersion = GetVersion(targetClientContext);
                baseTransformationInformation.TargetVersionNumber = GetExactVersion(targetClientContext);
            }

            if (sourceClientContext != null && targetClientContext == null)
            {
                baseTransformationInformation.TargetVersion = baseTransformationInformation.SourceVersion;
                baseTransformationInformation.TargetVersionNumber = baseTransformationInformation.SourceVersionNumber;
            }

        }

        internal bool IsWikiPage(string pageType)
        {
            return pageType.Equals("WikiPage", StringComparison.InvariantCultureIgnoreCase);
        }

        internal bool IsPublishingPage(string pageType)
        {
            return pageType.Equals("PublishingPage", StringComparison.InvariantCultureIgnoreCase);
        }

        internal bool IsWebPartPage(string pageType)
        {
            return pageType.Equals("WebPartPage", StringComparison.InvariantCultureIgnoreCase);
        }

        internal bool IsBlogPage(string pageType)
        {
            return pageType.Equals("BlogPage", StringComparison.InvariantCultureIgnoreCase);
        }

        internal bool IsClientSidePage(string pageType)
        {
            return pageType.Equals("ClientSidePage", StringComparison.InvariantCultureIgnoreCase);
        }

        internal bool IsAspxPage(string pageType)
        {
            return pageType.Equals("AspxPage", StringComparison.InvariantCultureIgnoreCase);
        }

        internal void StoreSourcePageInformationToKeep(ListItem sourcePage)
        {
            this.SourcePageAuthor = sourcePage[Constants.CreatedByField] as FieldUserValue;
            this.SourcePageEditor = sourcePage[Constants.ModifiedByField] as FieldUserValue;

            // Ensure to interprete time correctly: SPO stores in UTC, but we'll need to push back in local
            if (DateTime.TryParse(sourcePage[Constants.CreatedField].ToString(), out DateTime created))
            {
                DateTime createdIsUtc = DateTime.SpecifyKind(created, DateTimeKind.Utc);
                this.SourcePageCreated = createdIsUtc.ToLocalTime();
            }
            if (DateTime.TryParse(sourcePage[Constants.ModifiedField].ToString(), out DateTime modified))
            {
                DateTime modifiedIsUtc = DateTime.SpecifyKind(modified, DateTimeKind.Utc);
                this.SourcePageModified = modifiedIsUtc.ToLocalTime();
            }
        }

        internal void UpdateTargetPageWithSourcePageInformation(ListItem targetPage, BaseTransformationInformation baseTransformationInformation, string serverRelativePathForModernPage, bool crossSiteTransformation)
        {
            try
            {
                FieldUserValue pageAuthor = this.SourcePageAuthor;
                FieldUserValue pageEditor = this.SourcePageEditor;
                bool isOwner = false;

                // Keeping page author information is only possible when staying in SPO...for cross site support we first do need user account mapping
                var sourcePlatformVersion = baseTransformationInformation.SourceVersion;

                if (crossSiteTransformation && baseTransformationInformation.KeepPageCreationModificationInformation && sourcePlatformVersion == SPVersion.SPO)
                {
                    // If transformtion is cross site collection we'll need to lookup users again
                    // Using a cloned context to not mess up with the pending list item updates
                    using (var clonedTargetContext = targetClientContext.Clone(targetClientContext.Web.GetUrl()))
                    {
                        var pageAuthorUser = clonedTargetContext.Web.EnsureUser(this.SourcePageAuthor.LookupValue);
                        var pageEditorUser = clonedTargetContext.Web.EnsureUser(this.SourcePageEditor.LookupValue);
                        clonedTargetContext.Load(pageAuthorUser);
                        clonedTargetContext.Load(pageEditorUser);
                        clonedTargetContext.ExecuteQueryRetry();

                        var currentUser = clonedTargetContext.Web.CurrentUser;
                        clonedTargetContext.Load(currentUser, c => c.LoginName);
                        clonedTargetContext.Load(clonedTargetContext.Web, w => w.EffectiveBasePermissions);
                        clonedTargetContext.ExecuteQueryRetry();

                        var permissions = clonedTargetContext.Web.GetUserEffectivePermissions(currentUser.LoginName);
                        clonedTargetContext.ExecuteQueryRetry();

                        isOwner = permissions.Value.Has(PermissionKind.ManagePermissions);

                        // Prep a new FieldUserValue object instance and update the list item
                        pageAuthor = new FieldUserValue()
                        {
                            LookupId = pageAuthorUser.Id
                        };

                        pageEditor = new FieldUserValue()
                        {
                            LookupId = pageEditorUser.Id
                        };
                    }
                }

                if (baseTransformationInformation.KeepPageCreationModificationInformation || baseTransformationInformation.PostAsNews)
                {
                    if (baseTransformationInformation.KeepPageCreationModificationInformation && sourcePlatformVersion == SPVersion.SPO)
                    {
                        // Set author/editor/modified/created fields only if you are owner, else they are not set.
                        if (isOwner)
                        {
                            // All 4 fields have to be set!
                            targetPage[Constants.CreatedByField] = pageAuthor;
                            targetPage[Constants.ModifiedByField] = pageEditor;
                            targetPage[Constants.CreatedField] = this.SourcePageCreated;
                            targetPage[Constants.ModifiedField] = this.SourcePageModified;
                        }
                        else
                        {
                            LogWarning("Since you are not the owner, the PageCreationModificationInformation can't be saved.", LogStrings.Heading_ArticlePageHandling);
                        }
                    }

                    if (baseTransformationInformation.PostAsNews)
                    {
                        targetPage[Constants.PromotedStateField] = "2";

                        // Determine what will be the publishing date that will show up in the news rollup
                        if (baseTransformationInformation.KeepPageCreationModificationInformation && sourcePlatformVersion == SPVersion.SPO)
                        {
                            targetPage[Constants.FirstPublishedDateField] = this.SourcePageModified;
                        }
                        else
                        {
                            targetPage[Constants.FirstPublishedDateField] = targetPage[Constants.ModifiedField];
                        }
                    }

                    targetPage.UpdateOverwriteVersion();

                    if (baseTransformationInformation.PublishCreatedPage)
                    {
                        var targetPageFile = ((targetPage.Context) as ClientContext).Web.GetFileByServerRelativeUrl(serverRelativePathForModernPage);
                        targetPage.Context.Load(targetPageFile);
                        // Try to publish, if publish is not needed/possible (e.g. when no minor/major versioning set) then this will return an error that we'll be ignoring
                        targetPageFile.Publish(LogStrings.PublishMessage);
                    }
                }

                targetPage.Context.ExecuteQueryRetry();
            }
            catch (Exception ex)
            {
                // Eat exceptions as this is not critical for the generated page
                LogWarning(LogStrings.Warning_NonCriticalErrorDuringPublish, LogStrings.Heading_ArticlePageHandling);
            }
        }
        #endregion


    }
}
