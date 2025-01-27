﻿using Microsoft.SharePoint.Administration;
using Microsoft.SharePoint.Administration.Claims;
using Microsoft.SharePoint.WebControls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Yvand.LdapClaimsProvider.Logging;
using WIF4_5 = System.Security.Claims;

namespace Yvand.LdapClaimsProvider.Configuration
{
    public interface ILdapProviderSettings
    {
        #region Base settings
        /// <summary>
        /// Gets the version of the settings
        /// </summary>
        long Version { get; }

        /// <summary>
        /// Gets the claim types and their mapping with a DirectoryObject property
        /// </summary>
        ClaimTypeConfigCollection ClaimTypes { get; }

        /// <summary>
        /// Gets whether to skip the requests to LDAP and consider any input as valid.
        /// This can be useful to keep people picker working even if connectivity with the directory is lost.
        /// </summary>
        bool AlwaysResolveUserInput { get; }

        /// <summary>
        /// Gets whether to return only results that match exactly the user input (case-insensitive).
        /// </summary>
        bool FilterExactMatchOnly { get; }

        /// <summary>
        /// Gets whether to return the groups the user is a member of.
        /// </summary>
        bool EnableAugmentation { get; }

        /// <summary>
        /// Gets the string that will appear as a prefix of the text of each result, in the people picker.
        /// </summary>
        string EntityDisplayTextPrefix { get; }

        /// <summary>
        /// Gets the timeout in seconds, before an operation to LDAP directory is canceled.
        /// </summary>
        int Timeout { get; }

        /// <summary>
        /// Gets this property, not used by LDAPCP and available to developers for their own needs
        /// </summary>
        string CustomData { get; }

        /// <summary>
        /// Gets how many results maximum can be returned to the people picker during a search operation
        /// </summary>
        int MaxSearchResultsCount { get; }
        #endregion

        #region LDAP-specific settings
        /// <summary>
        /// Gets the list of LDAP directories
        /// </summary>
        List<DirectoryConnection> LdapConnections { get; }
        bool FilterEnabledUsersOnly { get; }
        bool FilterSecurityGroupsOnly { get; }
        bool AddWildcardAsPrefixOfInput { get; }
        #endregion
    }

    public class LdapProviderSettings : ILdapProviderSettings
    {
        #region Base settings
        public long Version { get; set; }
        public ClaimTypeConfigCollection ClaimTypes { get; set; }
        public bool AlwaysResolveUserInput { get; set; } = false;
        public bool FilterExactMatchOnly { get; set; } = false;
        public bool EnableAugmentation { get; set; } = true;
        public string EntityDisplayTextPrefix { get; set; }
        public int Timeout { get; set; } = ClaimsProviderConstants.DEFAULT_TIMEOUT;
        public string CustomData { get; set; }
        public int MaxSearchResultsCount { get; set; } = -1;
        #endregion

        #region LDAP specific settings
        public List<DirectoryConnection> LdapConnections { get; set; } = new List<DirectoryConnection>();
        public bool FilterEnabledUsersOnly { get; set; } = true;
        public bool FilterSecurityGroupsOnly { get; set; } = true;
        public bool AddWildcardAsPrefixOfInput { get; set; } = false;

        #endregion

        public LdapProviderSettings() { }

        public static LdapProviderSettings GetDefaultSettings(string claimsProviderName)
        {
            LdapProviderSettings entityProviderSettings = new LdapProviderSettings
            {
                ClaimTypes = LdapProviderSettings.ReturnDefaultClaimTypesConfig(claimsProviderName),
                LdapConnections = new List<DirectoryConnection>()
                {
                    new DirectoryConnection(true),
                }
            };
            return entityProviderSettings;
        }

        /// <summary>
        /// Returns the default claim types configuration list, based on the identity claim type set in the TrustedLoginProvider associated with <paramref name="claimProviderName"/>
        /// </summary>
        /// <returns></returns>
        public static ClaimTypeConfigCollection ReturnDefaultClaimTypesConfig(string claimsProviderName)
        {
            if (String.IsNullOrWhiteSpace(claimsProviderName))
            {
                throw new ArgumentNullException(nameof(claimsProviderName));
            }

            SPTrustedLoginProvider spTrust = Utils.GetSPTrustAssociatedWithClaimsProvider(claimsProviderName);
            if (spTrust == null)
            {
                Logger.Log($"No SPTrustedLoginProvider associated with claims provider '{claimsProviderName}' was found.", TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Core);
                return null;
            }

            // Add only the identity claim type for user claim type.
            // Then add the rest of user details as mapped to the identity claim type
            // Then add the group claim type
            ClaimTypeConfigCollection newCTConfigCollection = new ClaimTypeConfigCollection(spTrust);
            ClaimTypeConfig ctConfig = ClaimsProviderConstants.GetDefaultSettingsPerUserClaimType(spTrust.IdentityClaimTypeInformation.MappedClaimType);
            if (ctConfig != null)
            {
                // identity claim type is well-known
                ctConfig.ClaimType = spTrust.IdentityClaimTypeInformation.MappedClaimType;
                newCTConfigCollection.Add(ctConfig);
            }
            else
            {
                // Unknown identity claim type
                ctConfig = new ClaimTypeConfig
                {
                    // https://github.com/Yvand/LDAPCP/issues/221: Set all required properties to avoid exception when validating config
                    ClaimType = spTrust.IdentityClaimTypeInformation.MappedClaimType,
                    DirectoryObjectClass = "user",
                    DirectoryObjectAttribute = "MUST-BE-SET",
                };
                newCTConfigCollection.Add(ctConfig);
            }
            // By default, do the same as AD claims provider: Show the displayName of users in the people picker list
            newCTConfigCollection.UserIdentifierConfig.DirectoryObjectAttributeForDisplayText = "displayName";


            // Not adding those as additional attributes to avoid having too many LDAP attributes to search users in the LDAP filter
            var nonIdentityClaimTypes = ClaimsProviderConstants
                .GetDefaultSettingsPerUserClaimType()
                .Where(x => 
                    !String.Equals(x.Key, spTrust.IdentityClaimTypeInformation.MappedClaimType, StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(x.Key, WIF4_5.ClaimTypes.PrimarySid, StringComparison.OrdinalIgnoreCase)
                    );
            foreach (var nonIdentityClaimType in nonIdentityClaimTypes)
            {
                ctConfig = nonIdentityClaimType.Value;
                ctConfig.ClaimType = String.Empty;
                ctConfig.IsAdditionalLdapSearchAttribute = true;
                newCTConfigCollection.Add(ctConfig);
            }

            // Additional properties to find user and create entity with the identity claim type (IsAdditionalLdapSearchAttribute=true)
            newCTConfigCollection.Add(new ClaimTypeConfig { DirectoryObjectType = DirectoryObjectType.User, DirectoryObjectClass = "user", DirectoryObjectAttribute = "displayName", IsAdditionalLdapSearchAttribute = true, SPEntityDataKey = PeopleEditorEntityDataKeys.DisplayName, DirectoryObjectAdditionalFilter = "(!(objectClass=computer))" });
            newCTConfigCollection.Add(new ClaimTypeConfig { DirectoryObjectType = DirectoryObjectType.User, DirectoryObjectClass = "user", DirectoryObjectAttribute = "cn", IsAdditionalLdapSearchAttribute = true, DirectoryObjectAdditionalFilter = "(!(objectClass=computer))" });
            newCTConfigCollection.Add(new ClaimTypeConfig { DirectoryObjectType = DirectoryObjectType.User, DirectoryObjectClass = "user", DirectoryObjectAttribute = "sn", IsAdditionalLdapSearchAttribute = true, DirectoryObjectAdditionalFilter = "(!(objectClass=computer))" });
            newCTConfigCollection.Add(new ClaimTypeConfig { DirectoryObjectType = DirectoryObjectType.User, DirectoryObjectClass = "user", DirectoryObjectAttribute = "givenName", IsAdditionalLdapSearchAttribute = true, DirectoryObjectAdditionalFilter = "(!(objectClass=computer))" });  // First name

            // Additional properties to populate metadata of entity created: no claim type set, SPEntityDataKey is set and IsAdditionalLdapSearchAttribute = false (default value)
            newCTConfigCollection.Add(new ClaimTypeConfig { DirectoryObjectType = DirectoryObjectType.User, DirectoryObjectClass = "user", DirectoryObjectAttribute = "physicalDeliveryOfficeName", IsAdditionalLdapSearchAttribute = false, SPEntityDataKey = PeopleEditorEntityDataKeys.Location, DirectoryObjectAdditionalFilter = "(!(objectClass=computer))" });
            newCTConfigCollection.Add(new ClaimTypeConfig { DirectoryObjectType = DirectoryObjectType.User, DirectoryObjectClass = "user", DirectoryObjectAttribute = "title", IsAdditionalLdapSearchAttribute = false, SPEntityDataKey = PeopleEditorEntityDataKeys.JobTitle, DirectoryObjectAdditionalFilter = "(!(objectClass=computer))" });
            newCTConfigCollection.Add(new ClaimTypeConfig { DirectoryObjectType = DirectoryObjectType.User, DirectoryObjectClass = "user", DirectoryObjectAttribute = "msRTCSIP-PrimaryUserAddress", IsAdditionalLdapSearchAttribute = false, SPEntityDataKey = PeopleEditorEntityDataKeys.SIPAddress, DirectoryObjectAdditionalFilter = "(!(objectClass=computer))" });
            newCTConfigCollection.Add(new ClaimTypeConfig { DirectoryObjectType = DirectoryObjectType.User, DirectoryObjectClass = "user", DirectoryObjectAttribute = "telephoneNumber", IsAdditionalLdapSearchAttribute = false, SPEntityDataKey = PeopleEditorEntityDataKeys.WorkPhone, DirectoryObjectAdditionalFilter = "(!(objectClass=computer))" });

            // Group
            newCTConfigCollection.Add(new ClaimTypeConfig { DirectoryObjectType = DirectoryObjectType.Group, DirectoryObjectClass = "group", DirectoryObjectAttribute = "sAMAccountName", ClaimType = ClaimsProviderConstants.DefaultMainGroupClaimType, ClaimValueLeadingToken = @"{fqdn}\" });
            newCTConfigCollection.Add(new ClaimTypeConfig { DirectoryObjectType = DirectoryObjectType.Group, DirectoryObjectClass = "group", DirectoryObjectAttribute = "displayName", IsAdditionalLdapSearchAttribute = true, SPEntityDataKey = PeopleEditorEntityDataKeys.DisplayName });
            newCTConfigCollection.Add(new ClaimTypeConfig { DirectoryObjectType = DirectoryObjectType.Group, DirectoryObjectClass = "group", DirectoryObjectAttribute = "mail", SPEntityDataKey = PeopleEditorEntityDataKeys.Email });

            // Special case of the LDAP attribute "primaryGroupID": It is a group permission in SPS, but in a user object in LDAP
            //newCTConfigCollection.Add(new ClaimTypeConfig { DirectoryObjectType = DirectoryObjectType.User, DirectoryObjectClass = "user", SPEntityType = ClaimsProviderConstants.GroupClaimEntityType, DirectoryObjectAttribute = "primaryGroupID", ClaimType = WIF4_5.ClaimTypes.PrimaryGroupSid, DirectoryObjectAttributeSupportsWildcard = false });

            return newCTConfigCollection;
        }
    }

    public class LdapProviderConfiguration : SPPersistedObject, ILdapProviderSettings
    {
        public string LocalAssemblyVersion => ClaimsProviderConstants.ClaimsProviderVersion;
        
        /// <summary>
        /// Gets or sets the version of the settings
        /// </summary>
        public ILdapProviderSettings Settings
        {
            get
            {
                if (_Settings == null)
                {
                    _Settings = GenerateSettingsFromCurrentConfiguration();
                }
                return _Settings;
            }
        }
        private ILdapProviderSettings _Settings;

        #region "Base settings implemented from IEntraIDEntityProviderSettings"

        /// <summary>
        /// Gets or sets the claim types and their mapping with a DirectoryObject property
        /// </summary>
        public ClaimTypeConfigCollection ClaimTypes
        {
            get
            {
                if (_ClaimTypes == null)
                {
                    _ClaimTypes = new ClaimTypeConfigCollection(ref this._ClaimTypesCollection, this.SPTrust);
                }
                return _ClaimTypes;
            }
            private set
            {
                _ClaimTypes = value;
                _ClaimTypesCollection = value == null ? null : value.innerCol;
            }
        }
        [Persisted]
        private Collection<ClaimTypeConfig> _ClaimTypesCollection;
        private ClaimTypeConfigCollection _ClaimTypes;

        /// <summary>
        /// Gets or sets whether to skip the requests to LDAP and consider any input as valid.
        /// This can be useful to keep people picker working even if connectivity with the directory is lost.
        /// </summary>
        public bool AlwaysResolveUserInput
        {
            get => _AlwaysResolveUserInput;
            private set => _AlwaysResolveUserInput = value;
        }
        [Persisted]
        private bool _AlwaysResolveUserInput = false;

        /// <summary>
        /// Gets or sets whether to return only results that match exactly the user input (case-insensitive).
        /// </summary>
        public bool FilterExactMatchOnly
        {
            get => _FilterExactMatchOnly;
            private set => _FilterExactMatchOnly = value;
        }
        [Persisted]
        private bool _FilterExactMatchOnly = false;

        /// <summary>
        /// Gets or sets whether to return the groups the user is a member of.
        /// </summary>
        public bool EnableAugmentation
        {
            get => _EnableAugmentation;
            private set => _EnableAugmentation = value;
        }
        [Persisted]
        private bool _EnableAugmentation = true;

        /// <summary>
        /// Gets or sets the string that will appear as a prefix of the text of each result, in the people picker.
        /// </summary>
        public string EntityDisplayTextPrefix
        {
            get => _EntityDisplayTextPrefix;
            private set => _EntityDisplayTextPrefix = value;
        }
        [Persisted]
        private string _EntityDisplayTextPrefix;

        /// <summary>
        /// Gets or sets the timeout in seconds, before an operation to LDAP directory is canceled.
        /// </summary>
        public int Timeout
        {
            get
            {
                return _Timeout;
            }
            private set => _Timeout = value;
        }
        [Persisted]
        private int _Timeout = ClaimsProviderConstants.DEFAULT_TIMEOUT;

        /// <summary>
        /// Gets or sets this property, not used by LDAPCP and available to developers for their own needs
        /// </summary>
        public string CustomData
        {
            get => _CustomData;
            private set => _CustomData = value;
        }
        [Persisted]
        private string _CustomData;

        /// <summary>
        /// Gets or sets how many results maximum can be returned to the people picker during a search operation
        /// </summary>
        public int MaxSearchResultsCount
        {
            get
            {
                return _MaxSearchResultsCount;
            }
            private set => _MaxSearchResultsCount = value;
        }
        [Persisted]
        private int _MaxSearchResultsCount = -1;
        #endregion


        #region "LDAP-specific settings implemented from IEntraIDEntityProviderSettings"
        /// <summary>
        /// Gets or sets the list of LDAP directories
        /// </summary>
        public List<DirectoryConnection> LdapConnections
        {
            get => _LdapServers;
            private set => _LdapServers = value;
        }
        [Persisted]
        private List<DirectoryConnection> _LdapServers;// = new List<DirectoryConnection>();

        public bool FilterEnabledUsersOnly
        {
            get => _FilterEnabledUsersOnly;
            private set => _FilterEnabledUsersOnly = value;
        }
        [Persisted]
        private bool _FilterEnabledUsersOnly = true;

        public bool FilterSecurityGroupsOnly
        {
            get => _FilterSecurityGroupsOnly;
            private set => _FilterSecurityGroupsOnly = value;
        }
        [Persisted]
        private bool _FilterSecurityGroupsOnly = true;

        public bool AddWildcardAsPrefixOfInput
        {
            get => _AddWildcardAsPrefixOfInput;
            private set => _AddWildcardAsPrefixOfInput = value;
        }
        [Persisted]
        private bool _AddWildcardAsPrefixOfInput = false;
        #endregion

        #region "Other properties"
        /// <summary>
        /// Gets or sets the name of the claims provider using this settings
        /// </summary>
        public string ClaimsProviderName
        {
            get => _ClaimsProviderName;
            set => _ClaimsProviderName = value;
        }
        [Persisted]
        private string _ClaimsProviderName;

        [Persisted]
        private string ClaimsProviderVersion;

        private SPTrustedLoginProvider _SPTrust;
        protected SPTrustedLoginProvider SPTrust
        {
            get
            {
                if (this._SPTrust == null)
                {
                    this._SPTrust = Utils.GetSPTrustAssociatedWithClaimsProvider(this.ClaimsProviderName);
                }
                return this._SPTrust;
            }
        }
        #endregion

        public LdapProviderConfiguration() { }
        public LdapProviderConfiguration(string persistedObjectName, SPPersistedObject parent, string claimsProviderName) : base(persistedObjectName, parent)
        {
            this.ClaimsProviderName = claimsProviderName;
            this.Initialize();
        }

        private void Initialize()
        {
            this.InitializeDefaultSettings();
        }

        public virtual bool InitializeDefaultSettings()
        {
            this.ClaimTypes = ReturnDefaultClaimTypesConfig();
            return true;
        }

        /// <summary>
        /// Returns a TSettings from the properties of the current persisted object
        /// </summary>
        /// <returns></returns>
        protected virtual ILdapProviderSettings GenerateSettingsFromCurrentConfiguration()
        {
            ILdapProviderSettings entityProviderSettings = new LdapProviderSettings()
            {
                AlwaysResolveUserInput = this.AlwaysResolveUserInput,
                ClaimTypes = this.ClaimTypes,
                CustomData = this.CustomData,
                EnableAugmentation = this.EnableAugmentation,
                EntityDisplayTextPrefix = this.EntityDisplayTextPrefix,
                FilterExactMatchOnly = this.FilterExactMatchOnly,
                Timeout = this.Timeout,
                MaxSearchResultsCount = this.MaxSearchResultsCount,

                Version = this.Version,

                // Properties specific to type IEntraSettings
                LdapConnections = this.LdapConnections,
                FilterEnabledUsersOnly = this.FilterEnabledUsersOnly,
                FilterSecurityGroupsOnly = this.FilterSecurityGroupsOnly,
                AddWildcardAsPrefixOfInput = this.AddWildcardAsPrefixOfInput,
            };
            return (ILdapProviderSettings)entityProviderSettings;
        }

        /// <summary>
        /// Gets the directory configuration
        /// </summary>
        /// <param name="directoryConnectionPath">Directory path</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public DirectoryConnection GetLdapConnection(string directoryConnectionPath)
        {
            if (String.IsNullOrWhiteSpace(directoryConnectionPath))
            {
                throw new ArgumentNullException(nameof(directoryConnectionPath));
            }
            return this.LdapConnections.FirstOrDefault(x => x.LdapPath.Equals(directoryConnectionPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Deletes the directory configuration
        /// </summary>
        /// <param name="directoryConnectionPath">Directory path</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public bool DeleteLdapConnection(string directoryConnectionPath)
        {
            if (String.IsNullOrWhiteSpace(directoryConnectionPath))
            {
                throw new ArgumentNullException(nameof(directoryConnectionPath));
            }

            DirectoryConnection directory = GetLdapConnection(directoryConnectionPath);
            if (directory == null) { return false; }
            return this.LdapConnections.Remove(directory);
        }

        /// <summary>
        /// If it is valid, commits the current settings to the SharePoint settings database
        /// </summary>
        public override void Update()
        {
            this.ValidateConfiguration();
            base.Update();
            Logger.Log($"Successfully updated configuration '{this.Name}' with Id {this.Id}", TraceSeverity.High, EventSeverity.Information, TraceCategory.Core);
        }

        /// <summary>
        /// If it is valid, commits the current settings to the SharePoint settings database
        /// </summary>
        /// <param name="ensure">If true, the call will not throw if the object already exists.</param>
        public override void Update(bool ensure)
        {
            this.ValidateConfiguration();
            base.Update(ensure);
            Logger.Log($"Successfully updated configuration '{this.Name}' with Id {this.Id}", TraceSeverity.High, EventSeverity.Information, TraceCategory.Core);
        }

        /// <summary>
        /// Ensures that the current configuration is valid and can be safely persisted in the configuration database
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        public virtual void ValidateConfiguration()
        {
            // In case ClaimTypes collection was modified, test if it is still valid
            if (this.ClaimTypes == null || this.ClaimTypes.Count == 0)
            {
                throw new InvalidOperationException($"Configuration is not valid because collection {nameof(ClaimTypes)} is null");
            }
            try
            {
                ClaimTypeConfigCollection testUpdateCollection = new ClaimTypeConfigCollection(this.SPTrust);
                foreach (ClaimTypeConfig curCTConfig in this.ClaimTypes)
                {
                    testUpdateCollection.Add(curCTConfig, false);
                }
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Some changes made to collection {nameof(ClaimTypes)} are invalid and cannot be committed to configuration database. Inspect inner exception for more details about the error.", ex);
            }

            // Ensure identity claim type is present and valid
            ClaimTypeConfig identityClaimTypeConfig = this.ClaimTypes.GetIdentifierConfiguration(DirectoryObjectType.User);
            if (identityClaimTypeConfig == null)
            {
                throw new InvalidOperationException($"The configuration is invalid because the identity claim type configuration is missing in the collection {nameof(ClaimTypes)}, so changes cannot be committed to the configuration database.");
            }

            foreach (DirectoryConnection server in this.LdapConnections)
            {
                if (server == null)
                {
                    throw new InvalidOperationException($"Configuration is not valid because a server is null in list {nameof(LdapConnections)}");
                }

                if (server.UseDefaultADConnection == false)
                {
                    if (String.IsNullOrWhiteSpace(server.LdapPath))
                    {
                        throw new InvalidOperationException($"Configuration is not valid because a server has its property {nameof(server.LdapPath)} not set in list {nameof(LdapConnections)}");
                    }

                    if (String.IsNullOrWhiteSpace(server.Username))
                    {
                        throw new InvalidOperationException($"Configuration is not valid because a server has its property {nameof(server.Username)} not set in list {nameof(LdapConnections)}");
                    }

                    if (String.IsNullOrWhiteSpace(server.Password))
                    {
                        throw new InvalidOperationException($"Configuration is not valid because a server has its property {nameof(server.Password)} not set in list {nameof(LdapConnections)}");
                    }
                }
            }

            if (MaxSearchResultsCount < -1)
            {
                throw new InvalidOperationException($"The configuration is invalid because the value of property {nameof(MaxSearchResultsCount)} is < -1");
            }
        }

        /// <summary>
        /// Removes the current persisted object from the SharePoint configuration database
        /// </summary>
        public override void Delete()
        {
            base.Delete();
            Logger.Log($"Successfully deleted configuration '{this.Name}' with Id {this.Id}", TraceSeverity.High, EventSeverity.Information, TraceCategory.Core);
        }

        /// <summary>
        /// Override this method to allow more users to update the object. True specifies that more users can update the object; otherwise, false. The default value is false.
        /// </summary>
        /// <returns></returns>
        protected override bool HasAdditionalUpdateAccess()
        {
            return false;
        }

        /// <summary>
        /// Applies the settings passed in parameter to the current settings
        /// </summary>
        /// <param name="settings"></param>
        public virtual void ApplySettings(ILdapProviderSettings settings, bool commitIfValid)
        {
            if (settings == null)
            {
                return;
            }

            if (settings.ClaimTypes == null)
            {
                this.ClaimTypes = null;
            }
            else
            {
                this.ClaimTypes = new ClaimTypeConfigCollection(this.SPTrust);
                foreach (ClaimTypeConfig claimTypeConfig in settings.ClaimTypes)
                {
                    this.ClaimTypes.Add(claimTypeConfig.CopyConfiguration(), false);
                }
            }
            this.AlwaysResolveUserInput = settings.AlwaysResolveUserInput;
            this.FilterExactMatchOnly = settings.FilterExactMatchOnly;
            this.EnableAugmentation = settings.EnableAugmentation;
            this.EntityDisplayTextPrefix = settings.EntityDisplayTextPrefix;
            this.Timeout = settings.Timeout;
            this.CustomData = settings.CustomData;
            this.MaxSearchResultsCount = settings.MaxSearchResultsCount;

            this.LdapConnections = settings.LdapConnections;
            this.FilterEnabledUsersOnly = settings.FilterEnabledUsersOnly;
            this.FilterSecurityGroupsOnly = settings.FilterSecurityGroupsOnly;
            this.AddWildcardAsPrefixOfInput = settings.AddWildcardAsPrefixOfInput;

            if (commitIfValid)
            {
                this.Update();
            }
        }

        public virtual ILdapProviderSettings GetDefaultSettings()
        {
            return LdapProviderSettings.GetDefaultSettings(this.ClaimsProviderName);
        }

        public virtual ClaimTypeConfigCollection ReturnDefaultClaimTypesConfig()
        {
            return LdapProviderSettings.ReturnDefaultClaimTypesConfig(this.ClaimsProviderName);
        }

        public void ResetClaimTypesList()
        {
            ClaimTypes.Clear();
            ClaimTypes = ReturnDefaultClaimTypesConfig();
            Logger.Log($"Claim types list of configuration '{Name}' was successfully reset to default configuration",
                TraceSeverity.High, EventSeverity.Information, TraceCategory.Core);
        }

        /// <summary>
        /// Returns the global configuration, stored as a persisted object in the SharePoint configuration database
        /// </summary>
        /// <param name="configurationId">The ID of the configuration</param>
        /// <param name="initializeLocalSettings">Set to true to initialize the property <see cref="Settings"/></param>
        /// <returns></returns>
        public static LdapProviderConfiguration GetGlobalConfiguration(Guid configurationId, bool initializeLocalSettings = false)
        {
            SPFarm parent = SPFarm.Local;
            try
            {
                LdapProviderConfiguration configuration = (LdapProviderConfiguration)parent.GetObject(configurationId);
                //if (configuration != null && initializeLocalSettings == true)
                //{
                //    configuration.RefreshSettingsIfNeeded();
                //}
                return configuration;
            }
            catch (Exception ex)
            {
                Logger.LogException(String.Empty, $"while retrieving configuration ID '{configurationId}'", TraceCategory.Configuration, ex);
            }
            return null;
        }

        public static void DeleteGlobalConfiguration(Guid configurationId)
        {
            LdapProviderConfiguration configuration = GetGlobalConfiguration(configurationId);
            if (configuration == null)
            {
                Logger.Log($"Configuration ID '{configurationId}' was not found in configuration database", TraceSeverity.Medium, EventSeverity.Error, TraceCategory.Core);
                return;
            }
            configuration.Delete();
            Logger.Log($"Configuration ID '{configurationId}' was successfully deleted from configuration database", TraceSeverity.High, EventSeverity.Information, TraceCategory.Core);
        }

        /// <summary>
        /// Creates a configuration. This will delete any existing configuration which may already exist
        /// </summary>
        /// <param name="configurationID">ID of the new configuration</param>
        /// <param name="configurationName">Name of the new configuration</param>
        /// <param name="claimsProviderName">Clais provider associated with this new configuration</param>
        /// <param name="T">Type of the new configuration</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static LdapProviderConfiguration CreateGlobalConfiguration(Guid configurationID, string configurationName, string claimsProviderName)
        {
            if (String.IsNullOrWhiteSpace(claimsProviderName))
            {
                throw new ArgumentNullException(nameof(claimsProviderName));
            }

            if (Utils.GetSPTrustAssociatedWithClaimsProvider(claimsProviderName) == null)
            {
                return null;
            }

            // Ensure it doesn't already exists and delete it if so
            LdapProviderConfiguration existingConfig = GetGlobalConfiguration(configurationID);
            if (existingConfig != null)
            {
                DeleteGlobalConfiguration(configurationID);
            }

            Logger.Log($"Creating configuration '{configurationName}' with Id {configurationID}...", TraceSeverity.VerboseEx, EventSeverity.Error, TraceCategory.Core);
            LdapProviderConfiguration globalConfiguration = new LdapProviderConfiguration(configurationName, SPFarm.Local, claimsProviderName);
            ILdapProviderSettings defaultSettings = globalConfiguration.GetDefaultSettings();
            globalConfiguration.ApplySettings(defaultSettings, false);
            globalConfiguration.Id = configurationID;
            globalConfiguration.Update(true);
            Logger.Log($"Created configuration '{configurationName}' with Id {globalConfiguration.Id}", TraceSeverity.High, EventSeverity.Information, TraceCategory.Core);
            return globalConfiguration;
        }
    }
}
