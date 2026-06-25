using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

#pragma warning disable CA1416

public sealed class ActiveDirectoryService
{
    private const int UserAccountControlDisabledFlag = 0x2;
    private const int UserAccountControlNormalAccount = 0x200;

    private readonly IConfiguration _configuration;
    private readonly ILogger<ActiveDirectoryService> _logger;

    public ActiveDirectoryService(IConfiguration configuration, ILogger<ActiveDirectoryService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsEnabled => _configuration.GetValue<bool>("ActiveDirectory:Enabled");

    public ActiveDirectoryUserLink CreateUser(string displayName, string email, string initialPassword)
    {
        if (!IsEnabled) return ActiveDirectoryUserLink.Disabled;

        var settings = ReadSettings(requireContainer: true, requireServiceAccount: true);
        return UseLdap(settings)
            ? CreateUserWithLdap(settings, displayName, email, initialPassword)
            : CreateUserWithAccountManagement(settings, displayName, email, initialPassword);
    }

    public bool ValidateCredentials(string login, string password, string? samAccountName, string? userPrincipalName)
    {
        if (!IsEnabled) return false;
        if (string.IsNullOrWhiteSpace(password)) return false;

        var settings = ReadSettings(requireContainer: false, requireServiceAccount: false);
        return UseLdap(settings)
            ? ValidateCredentialsWithLdap(settings, login, password, samAccountName, userPrincipalName)
            : ValidateCredentialsWithAccountManagement(settings, login, password, samAccountName, userPrincipalName);
    }

    public void SetPassword(string samAccountName, string? userPrincipalName, string newPassword)
    {
        if (!IsEnabled)
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Configuration, "Active Directory n'est pas active.");

        var settings = ReadSettings(requireContainer: true, requireServiceAccount: true);
        if (UseLdap(settings))
        {
            SetPasswordWithLdap(settings, samAccountName, userPrincipalName, newPassword);
            return;
        }

        SetPasswordWithAccountManagement(settings, samAccountName, userPrincipalName, newPassword);
    }

    public void SetAccountEnabled(string samAccountName, string? userPrincipalName, bool enabled)
    {
        if (!IsEnabled)
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Configuration, "Active Directory n'est pas active.");

        var settings = ReadSettings(requireContainer: true, requireServiceAccount: true);
        if (!enabled && string.IsNullOrWhiteSpace(settings.DisabledUsersContainerDistinguishedName))
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Configuration, "ActiveDirectory:DisabledUsersContainerDistinguishedName est requis pour desactiver un compte.");

        if (UseLdap(settings))
        {
            SetAccountEnabledWithLdap(settings, samAccountName, userPrincipalName, enabled);
            return;
        }

        SetAccountEnabledWithAccountManagement(settings, samAccountName, userPrincipalName, enabled);
    }

    private ActiveDirectoryUserLink CreateUserWithAccountManagement(ActiveDirectorySettings settings, string displayName, string email, string initialPassword)
    {
        EnsureWindowsForAccountManagement();

        using var createContext = CreateUserContainerContext(settings);
        using var searchContext = CreateDomainContext(settings, useServiceAccount: true);

        var upn = BuildUserPrincipalName(settings, email);
        if (FindUser(searchContext, IdentityType.UserPrincipalName, upn) != null)
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Duplicate, $"Un compte AD existe deja pour {upn}.");

        var sam = FindAvailableSamAccountName(searchContext, BuildSamBase(displayName, email));

        try
        {
            using var user = new UserPrincipal(createContext, sam, initialPassword, true)
            {
                UserPrincipalName = upn,
                Name = displayName,
                DisplayName = displayName,
                EmailAddress = email
            };

            user.Save();

            var guid = user.Guid?.ToString("D") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(guid))
                throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Unavailable, "Le GUID AD n'a pas ete retourne apres creation.");

            return new ActiveDirectoryUserLink(sam, upn, guid, true);
        }
        catch (PasswordException ex)
        {
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Validation, "Mot de passe refuse par la politique Active Directory.", ex);
        }
        catch (PrincipalExistsException ex)
        {
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Duplicate, "Un compte AD identique existe deja.", ex);
        }
        catch (PrincipalException ex)
        {
            _logger.LogError(ex, "Active Directory user creation failed for {Upn}", upn);
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Unavailable, "Creation du compte Active Directory impossible.", ex);
        }
    }

    private bool ValidateCredentialsWithAccountManagement(ActiveDirectorySettings settings, string login, string password, string? samAccountName, string? userPrincipalName)
    {
        EnsureWindowsForAccountManagement();

        using var context = CreateDomainContext(settings, useServiceAccount: false);
        var candidates = BuildLoginCandidates(login, samAccountName, userPrincipalName, settings.NetBiosName).ToArray();

        foreach (var candidate in candidates)
        {
            try
            {
                if (context.ValidateCredentials(candidate, password, ContextOptions.Negotiate))
                    return true;
            }
            catch (PrincipalException ex)
            {
                _logger.LogWarning(ex, "Active Directory credential validation failed for {Candidate}", candidate);
                throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Unavailable, "Validation Active Directory indisponible.", ex);
            }
        }

        return false;
    }

    private void SetPasswordWithAccountManagement(ActiveDirectorySettings settings, string samAccountName, string? userPrincipalName, string newPassword)
    {
        EnsureWindowsForAccountManagement();

        using var context = CreateDomainContext(settings, useServiceAccount: true);
        using var user = FindUser(context, IdentityType.SamAccountName, samAccountName)
            ?? (!string.IsNullOrWhiteSpace(userPrincipalName) ? FindUser(context, IdentityType.UserPrincipalName, userPrincipalName) : null);

        if (user == null)
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.NotFound, "Compte Active Directory introuvable.");

        try
        {
            user.SetPassword(newPassword);
            user.Save();
        }
        catch (PasswordException ex)
        {
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Validation, "Mot de passe refuse par la politique Active Directory.", ex);
        }
        catch (PrincipalException ex)
        {
            _logger.LogError(ex, "Active Directory password reset failed for {SamAccountName}", samAccountName);
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Unavailable, "Reinitialisation AD impossible.", ex);
        }
    }

    private void SetAccountEnabledWithAccountManagement(ActiveDirectorySettings settings, string samAccountName, string? userPrincipalName, bool enabled)
    {
        EnsureWindowsForAccountManagement();

        using var context = CreateDomainContext(settings, useServiceAccount: true);
        using var user = FindUser(context, IdentityType.SamAccountName, samAccountName)
            ?? (!string.IsNullOrWhiteSpace(userPrincipalName) ? FindUser(context, IdentityType.UserPrincipalName, userPrincipalName) : null);

        if (user == null)
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.NotFound, "Compte Active Directory introuvable.");

        try
        {
            user.Enabled = enabled;
            user.Save();

            var targetContainer = enabled
                ? settings.UsersContainerDistinguishedName
                : settings.DisabledUsersContainerDistinguishedName;
            MoveUserToContainer(user, settings, targetContainer);
        }
        catch (PrincipalException ex)
        {
            _logger.LogError(ex, "Active Directory status update failed for {SamAccountName}", samAccountName);
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Unavailable, "Mise a jour du statut AD impossible.", ex);
        }
        catch (DirectoryServicesCOMException ex)
        {
            _logger.LogError(ex, "Active Directory move failed for {SamAccountName}", samAccountName);
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Unavailable, "Deplacement du compte AD impossible.", ex);
        }
    }

    private ActiveDirectoryUserLink CreateUserWithLdap(ActiveDirectorySettings settings, string displayName, string email, string initialPassword)
    {
        EnsureLdapsForPasswordOperations(settings);

        var upn = BuildUserPrincipalName(settings, email);
        using var connection = CreateLdapConnection(settings, useServiceAccount: true);

        if (FindLdapUser(connection, settings, samAccountName: null, userPrincipalName: upn) != null)
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Duplicate, $"Un compte AD existe deja pour {upn}.");

        var sam = FindAvailableSamAccountName(connection, settings, BuildSamBase(displayName, email));
        var cn = sam;
        var userDn = $"CN={EscapeDnValue(cn)},{settings.UsersContainerDistinguishedName}";

        try
        {
            var add = new AddRequest(
                userDn,
                new DirectoryAttribute("objectClass", "top", "person", "organizationalPerson", "user"),
                new DirectoryAttribute("cn", cn),
                new DirectoryAttribute("sAMAccountName", sam),
                new DirectoryAttribute("userPrincipalName", upn),
                new DirectoryAttribute("displayName", displayName),
                new DirectoryAttribute("mail", email),
                new DirectoryAttribute("userAccountControl", (UserAccountControlNormalAccount | UserAccountControlDisabledFlag).ToString(CultureInfo.InvariantCulture)));

            connection.SendRequest(add);
            SetLdapPassword(connection, userDn, initialPassword);
            ReplaceLdapAttribute(connection, userDn, "userAccountControl", UserAccountControlNormalAccount.ToString(CultureInfo.InvariantCulture));

            var user = FindLdapUser(connection, settings, sam, upn)
                ?? throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Unavailable, "Compte AD cree mais impossible a relire.");
            var guid = GetObjectGuid(user);
            if (string.IsNullOrWhiteSpace(guid))
                throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Unavailable, "Le GUID AD n'a pas ete retourne apres creation.");

            return new ActiveDirectoryUserLink(sam, upn, guid, true);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.EntryAlreadyExists)
        {
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Duplicate, "Un compte AD identique existe deja.", ex);
        }
        catch (DirectoryOperationException ex) when (IsPasswordPolicyError(ex))
        {
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Validation, "Mot de passe refuse par la politique Active Directory.", ex);
        }
        catch (DirectoryException ex)
        {
            _logger.LogError(ex, "LDAP user creation failed for {Upn}", upn);
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Unavailable, "Creation du compte Active Directory impossible via LDAP.", ex);
        }
    }

    private bool ValidateCredentialsWithLdap(ActiveDirectorySettings settings, string login, string password, string? samAccountName, string? userPrincipalName)
    {
        var candidates = BuildLoginCandidates(login, samAccountName, userPrincipalName, settings.NetBiosName).ToArray();

        foreach (var candidate in candidates)
        {
            try
            {
                using var connection = CreateLdapConnection(settings, useServiceAccount: false, bindUser: candidate, bindPassword: password);
                return true;
            }
            catch (LdapException ex) when (IsInvalidCredentials(ex))
            {
                continue;
            }
            catch (DirectoryException ex)
            {
                _logger.LogWarning(ex, "LDAP credential validation failed for {Candidate}", candidate);
                throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Unavailable, "Validation Active Directory indisponible via LDAP.", ex);
            }
        }

        return false;
    }

    private void SetPasswordWithLdap(ActiveDirectorySettings settings, string samAccountName, string? userPrincipalName, string newPassword)
    {
        EnsureLdapsForPasswordOperations(settings);

        using var connection = CreateLdapConnection(settings, useServiceAccount: true);
        var user = FindLdapUser(connection, settings, samAccountName, userPrincipalName)
            ?? throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.NotFound, "Compte Active Directory introuvable.");

        try
        {
            SetLdapPassword(connection, user.DistinguishedName, newPassword);
        }
        catch (DirectoryOperationException ex) when (IsPasswordPolicyError(ex))
        {
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Validation, "Mot de passe refuse par la politique Active Directory.", ex);
        }
        catch (DirectoryException ex)
        {
            _logger.LogError(ex, "LDAP password reset failed for {SamAccountName}", samAccountName);
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Unavailable, "Reinitialisation AD impossible via LDAP.", ex);
        }
    }

    private void SetAccountEnabledWithLdap(ActiveDirectorySettings settings, string samAccountName, string? userPrincipalName, bool enabled)
    {
        using var connection = CreateLdapConnection(settings, useServiceAccount: true);
        var user = FindLdapUser(connection, settings, samAccountName, userPrincipalName)
            ?? throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.NotFound, "Compte Active Directory introuvable.");

        try
        {
            var currentUac = GetIntAttribute(user, "userAccountControl", UserAccountControlNormalAccount);
            var newUac = enabled
                ? currentUac & ~UserAccountControlDisabledFlag
                : currentUac | UserAccountControlDisabledFlag;

            ReplaceLdapAttribute(connection, user.DistinguishedName, "userAccountControl", newUac.ToString(CultureInfo.InvariantCulture));

            var targetContainer = enabled
                ? settings.UsersContainerDistinguishedName
                : settings.DisabledUsersContainerDistinguishedName;
            MoveLdapUserToContainer(connection, user.DistinguishedName, targetContainer);
        }
        catch (DirectoryException ex)
        {
            _logger.LogError(ex, "LDAP status update failed for {SamAccountName}", samAccountName);
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Unavailable, "Mise a jour du statut AD impossible via LDAP.", ex);
        }
    }

    private PrincipalContext CreateUserContainerContext(ActiveDirectorySettings settings)
    {
        return new PrincipalContext(
            ContextType.Domain,
            settings.ServerOrDomain,
            settings.UsersContainerDistinguishedName,
            ContextOptions.Negotiate,
            NormalizeServiceAccountUser(settings),
            settings.ServiceAccountPassword);
    }

    private PrincipalContext CreateDomainContext(ActiveDirectorySettings settings, bool useServiceAccount)
    {
        if (useServiceAccount)
            return new PrincipalContext(ContextType.Domain, settings.ServerOrDomain, null, ContextOptions.Negotiate, NormalizeServiceAccountUser(settings), settings.ServiceAccountPassword);

        return new PrincipalContext(ContextType.Domain, settings.ServerOrDomain);
    }

    private LdapConnection CreateLdapConnection(ActiveDirectorySettings settings, bool useServiceAccount, string? bindUser = null, string? bindPassword = null)
    {
        var identifier = new LdapDirectoryIdentifier(settings.LdapHost, settings.LdapPort, fullyQualifiedDnsHostName: false, connectionless: false);
        var credential = useServiceAccount
            ? new NetworkCredential(NormalizeServiceAccountUser(settings), settings.ServiceAccountPassword)
            : new NetworkCredential(bindUser ?? string.Empty, bindPassword ?? string.Empty);

        var connection = new LdapConnection(identifier, credential, AuthType.Basic)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, settings.LdapTimeoutSeconds))
        };

        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = settings.UseLdaps;
        if (settings.IgnoreCertificateErrors)
            connection.SessionOptions.VerifyServerCertificate += (_, _) => true;

        connection.Bind();
        return connection;
    }

    private SearchResultEntry? FindLdapUser(LdapConnection connection, ActiveDirectorySettings settings, string? samAccountName, string? userPrincipalName)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(samAccountName))
            filters.Add($"(sAMAccountName={EscapeLdapFilter(samAccountName)})");
        if (!string.IsNullOrWhiteSpace(userPrincipalName))
            filters.Add($"(userPrincipalName={EscapeLdapFilter(userPrincipalName)})");
        if (filters.Count == 0) return null;

        var filter = filters.Count == 1 ? filters[0] : $"(|{string.Join(string.Empty, filters)})";
        var request = new SearchRequest(
            settings.BaseDistinguishedName,
            $"(&(objectClass=user){filter})",
            System.DirectoryServices.Protocols.SearchScope.Subtree,
            "distinguishedName",
            "objectGUID",
            "sAMAccountName",
            "userPrincipalName",
            "userAccountControl",
            "cn");

        var response = (SearchResponse)connection.SendRequest(request);
        if (response.Entries.Count == 0) return null;
        if (response.Entries.Count > 1)
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Duplicate, "Plusieurs comptes AD correspondent a cet identifiant.");

        return response.Entries[0];
    }

    private string FindAvailableSamAccountName(LdapConnection connection, ActiveDirectorySettings settings, string baseName)
    {
        var cleanBase = string.IsNullOrWhiteSpace(baseName) ? "user" : baseName;
        cleanBase = cleanBase.Length > 18 ? cleanBase[..18] : cleanBase;

        for (var i = 0; i < 100; i++)
        {
            var suffix = i == 0 ? string.Empty : i.ToString(CultureInfo.InvariantCulture);
            var maxBaseLength = Math.Min(20 - suffix.Length, cleanBase.Length);
            var candidate = cleanBase[..maxBaseLength] + suffix;
            if (FindLdapUser(connection, settings, candidate, userPrincipalName: null) == null)
                return candidate;
        }

        throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Duplicate, "Impossible de generer un sAMAccountName disponible.");
    }

    private static void SetLdapPassword(LdapConnection connection, string distinguishedName, string password)
    {
        var quotedPassword = Encoding.Unicode.GetBytes($"\"{password}\"");
        var request = new ModifyRequest(distinguishedName, DirectoryAttributeOperation.Replace, "unicodePwd", quotedPassword);
        connection.SendRequest(request);
    }

    private static void ReplaceLdapAttribute(LdapConnection connection, string distinguishedName, string attributeName, string value)
    {
        var request = new ModifyRequest(distinguishedName, DirectoryAttributeOperation.Replace, attributeName, value);
        connection.SendRequest(request);
    }

    private static void MoveLdapUserToContainer(LdapConnection connection, string distinguishedName, string targetContainerDn)
    {
        if (string.IsNullOrWhiteSpace(targetContainerDn)) return;
        if (IsUnderContainer(distinguishedName, targetContainerDn)) return;

        var rdn = GetRdn(distinguishedName);
        var request = new ModifyDNRequest(distinguishedName, targetContainerDn, rdn)
        {
            DeleteOldRdn = true
        };
        connection.SendRequest(request);
    }

    private static void MoveUserToContainer(UserPrincipal user, ActiveDirectorySettings settings, string targetContainerDn)
    {
        if (string.IsNullOrWhiteSpace(targetContainerDn)) return;

        using var entry = (DirectoryEntry)user.GetUnderlyingObject();
        var currentDn = entry.Properties["distinguishedName"]?.Value?.ToString() ?? string.Empty;
        if (IsUnderContainer(currentDn, targetContainerDn))
            return;

        using var target = new DirectoryEntry($"LDAP://{settings.ServerOrDomain}/{targetContainerDn}", NormalizeServiceAccountUser(settings), settings.ServiceAccountPassword);
        entry.MoveTo(target);
        entry.CommitChanges();
    }

    private ActiveDirectorySettings ReadSettings(bool requireContainer, bool requireServiceAccount)
    {
        var section = _configuration.GetSection("ActiveDirectory");
        var mode = (section["Mode"] ?? "Auto").Trim();
        var domainDns = (section["DomainDnsName"] ?? string.Empty).Trim();
        var netBios = (section["NetBiosName"] ?? string.Empty).Trim();
        var controller = (section["DomainController"] ?? string.Empty).Trim();
        var controllerIp = (section["DomainControllerIp"] ?? string.Empty).Trim();
        var baseDn = (section["BaseDistinguishedName"] ?? string.Empty).Trim();
        var container = (section["UsersContainerDistinguishedName"] ?? string.Empty).Trim();
        var disabledContainer = (section["DisabledUsersContainerDistinguishedName"] ?? string.Empty).Trim();
        var useLdaps = section.GetValue("UseLdaps", true);
        var ldapPort = section.GetValue<int?>("LdapPort") ?? (useLdaps ? 636 : 389);
        var ldapTimeoutSeconds = section.GetValue("LdapTimeoutSeconds", 10);
        var ignoreCertificateErrors = section.GetValue("IgnoreCertificateErrors", false);
        var serviceUserEnv = section["ServiceAccountUserEnvironmentVariable"] ?? "AD_SERVICE_USERNAME";
        var servicePasswordEnv = section["ServiceAccountPasswordEnvironmentVariable"] ?? "AD_SERVICE_PASSWORD";
        var serviceUser = (Environment.GetEnvironmentVariable(serviceUserEnv) ?? string.Empty).Trim();
        var servicePassword = Environment.GetEnvironmentVariable(servicePasswordEnv) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(domainDns))
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Configuration, "ActiveDirectory:DomainDnsName est requis.");
        if (requireContainer && string.IsNullOrWhiteSpace(container))
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Configuration, "ActiveDirectory:UsersContainerDistinguishedName est requis.");
        if (requireServiceAccount && (string.IsNullOrWhiteSpace(serviceUser) || string.IsNullOrWhiteSpace(servicePassword)))
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Configuration, $"Variables d'environnement {serviceUserEnv} et {servicePasswordEnv} requises.");

        if (string.IsNullOrWhiteSpace(baseDn))
            baseDn = DomainToDistinguishedName(domainDns);

        return new ActiveDirectorySettings(
            mode,
            domainDns,
            netBios,
            controller,
            controllerIp,
            baseDn,
            container,
            disabledContainer,
            useLdaps,
            ldapPort,
            ldapTimeoutSeconds,
            ignoreCertificateErrors,
            serviceUser,
            servicePassword);
    }

    private static bool UseLdap(ActiveDirectorySettings settings)
    {
        if (settings.Mode.Equals("Ldap", StringComparison.OrdinalIgnoreCase))
            return true;
        if (settings.Mode.Equals("AccountManagement", StringComparison.OrdinalIgnoreCase))
            return false;

        return !OperatingSystem.IsWindows();
    }

    private static void EnsureWindowsForAccountManagement()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ActiveDirectoryOperationException(
                ActiveDirectoryErrorKind.Configuration,
                "Le mode AccountManagement exige que le process .NET tourne sur Windows. Mettez ActiveDirectory:Mode a Ldap si l'API est lancee sous Linux/Nginx.");
        }
    }

    private static void EnsureLdapsForPasswordOperations(ActiveDirectorySettings settings)
    {
        if (!settings.UseLdaps)
        {
            throw new ActiveDirectoryOperationException(
                ActiveDirectoryErrorKind.Configuration,
                "LDAP simple ne suffit pas pour creer ou modifier un mot de passe AD. Activez LDAPS avec ActiveDirectory:UseLdaps=true et le port 636.");
        }
    }

    private static UserPrincipal? FindUser(PrincipalContext context, IdentityType identityType, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return UserPrincipal.FindByIdentity(context, identityType, value);
    }

    private string FindAvailableSamAccountName(PrincipalContext context, string baseName)
    {
        var cleanBase = string.IsNullOrWhiteSpace(baseName) ? "user" : baseName;
        cleanBase = cleanBase.Length > 18 ? cleanBase[..18] : cleanBase;

        for (var i = 0; i < 100; i++)
        {
            var suffix = i == 0 ? string.Empty : i.ToString(CultureInfo.InvariantCulture);
            var maxBaseLength = Math.Min(20 - suffix.Length, cleanBase.Length);
            var candidate = cleanBase[..maxBaseLength] + suffix;
            if (FindUser(context, IdentityType.SamAccountName, candidate) == null)
                return candidate;
        }

        throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Duplicate, "Impossible de generer un sAMAccountName disponible.");
    }

    private static string BuildUserPrincipalName(ActiveDirectorySettings settings, string email)
    {
        if (email.Contains('@')) return email.Trim();
        return $"{BuildSamBase(email, email)}@{settings.DomainDnsName}";
    }

    private static IEnumerable<string> BuildLoginCandidates(string login, string? samAccountName, string? userPrincipalName, string netBiosName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[] { userPrincipalName, samAccountName, login })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate.Trim()))
                yield return candidate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(netBiosName) && !string.IsNullOrWhiteSpace(samAccountName))
        {
            var downLevel = $"{netBiosName}\\{samAccountName}";
            if (seen.Add(downLevel)) yield return downLevel;
        }
    }

    private static string NormalizeServiceAccountUser(ActiveDirectorySettings settings)
    {
        var user = settings.ServiceAccountUser;
        if (string.IsNullOrWhiteSpace(user) || user.Contains('\\') || user.Contains('@'))
            return user;

        if (!string.IsNullOrWhiteSpace(settings.NetBiosName))
            return $"{settings.NetBiosName}\\{user}";

        return $"{user}@{settings.DomainDnsName}";
    }

    private static string BuildSamBase(string displayName, string email)
    {
        var source = email.Contains('@') ? email.Split('@')[0] : displayName;
        source = RemoveDiacritics(source).ToLowerInvariant();
        source = Regex.Replace(source, "[^a-z0-9._-]", ".");
        source = Regex.Replace(source, "[.]{2,}", ".");
        source = source.Trim('.', '-', '_');
        return string.IsNullOrWhiteSpace(source) ? "user" : source;
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(capacity: normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string DomainToDistinguishedName(string domainDnsName)
    {
        return string.Join(",", domainDnsName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => $"DC={EscapeDnValue(part)}"));
    }

    private static string EscapeLdapFilter(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '\\' => "\\5c",
                '*' => "\\2a",
                '(' => "\\28",
                ')' => "\\29",
                '\0' => "\\00",
                _ => c
            });
        }
        return builder.ToString();
    }

    private static string EscapeDnValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var mustEscape = c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '='
                || (i == 0 && (c == ' ' || c == '#'))
                || (i == value.Length - 1 && c == ' ');

            if (mustEscape) builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static bool IsUnderContainer(string distinguishedName, string containerDn)
    {
        return distinguishedName.EndsWith($",{containerDn}", StringComparison.OrdinalIgnoreCase)
            || distinguishedName.Equals(containerDn, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRdn(string distinguishedName)
    {
        var escaped = false;
        for (var i = 0; i < distinguishedName.Length; i++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (distinguishedName[i] == '\\')
            {
                escaped = true;
                continue;
            }

            if (distinguishedName[i] == ',')
                return distinguishedName[..i];
        }

        return distinguishedName;
    }

    private static string? GetStringAttribute(SearchResultEntry entry, string attributeName)
    {
        if (!entry.Attributes.Contains(attributeName) || entry.Attributes[attributeName].Count == 0)
            return null;

        return entry.Attributes[attributeName][0]?.ToString();
    }

    private static int GetIntAttribute(SearchResultEntry entry, string attributeName, int fallback)
    {
        var value = GetStringAttribute(entry, attributeName);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static string GetObjectGuid(SearchResultEntry entry)
    {
        if (!entry.Attributes.Contains("objectGUID") || entry.Attributes["objectGUID"].Count == 0)
            return string.Empty;

        var value = entry.Attributes["objectGUID"][0];
        return value is byte[] bytes ? new Guid(bytes).ToString("D") : string.Empty;
    }

    private static bool IsPasswordPolicyError(DirectoryOperationException ex)
    {
        return ex.Response?.ResultCode is ResultCode.ConstraintViolation or ResultCode.UnwillingToPerform;
    }

    private static bool IsInvalidCredentials(LdapException ex)
    {
        return ex.ErrorCode == 49;
    }

    private sealed record ActiveDirectorySettings(
        string Mode,
        string DomainDnsName,
        string NetBiosName,
        string DomainController,
        string DomainControllerIp,
        string BaseDistinguishedName,
        string UsersContainerDistinguishedName,
        string DisabledUsersContainerDistinguishedName,
        bool UseLdaps,
        int LdapPort,
        int LdapTimeoutSeconds,
        bool IgnoreCertificateErrors,
        string ServiceAccountUser,
        string ServiceAccountPassword)
    {
        public string ServerOrDomain => !string.IsNullOrWhiteSpace(DomainController) ? DomainController : DomainDnsName;
        public string LdapHost => !string.IsNullOrWhiteSpace(DomainController) ? DomainController : (!string.IsNullOrWhiteSpace(DomainControllerIp) ? DomainControllerIp : DomainDnsName);
    }
}

public sealed record ActiveDirectoryUserLink(string? SamAccountName, string? UserPrincipalName, string? ObjectGuid, bool Created)
{
    public static ActiveDirectoryUserLink Disabled { get; } = new(null, null, null, false);
}

public enum ActiveDirectoryErrorKind
{
    Configuration,
    Duplicate,
    NotFound,
    Validation,
    Unavailable
}

public sealed class ActiveDirectoryOperationException : Exception
{
    public ActiveDirectoryErrorKind Kind { get; }

    public ActiveDirectoryOperationException(ActiveDirectoryErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }
}
