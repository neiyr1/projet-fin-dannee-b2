using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

#pragma warning disable CA1416

public sealed class ActiveDirectoryService
{
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
        EnsureWindows();

        var settings = ReadSettings(requireContainer: true, requireServiceAccount: true);
        using var createContext = CreateUserContainerContext(settings);
        using var searchContext = CreateDomainContext(settings, useServiceAccount: true);

        var upn = BuildUserPrincipalName(settings, email);
        if (FindUser(searchContext, IdentityType.UserPrincipalName, upn) != null)
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Duplicate, $"Un compte AD existe deja pour {upn}.");

        var sam = FindAvailableSamAccountName(searchContext, BuildSamBase(displayName, email));

        try
        {
            using var user = new UserPrincipal(createContext)
            {
                SamAccountName = sam,
                UserPrincipalName = upn,
                Name = displayName,
                DisplayName = displayName,
                EmailAddress = email,
                Enabled = true
            };

            user.SetPassword(initialPassword);
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

    public bool ValidateCredentials(string login, string password, string? samAccountName, string? userPrincipalName)
    {
        if (!IsEnabled) return false;
        EnsureWindows();
        if (string.IsNullOrWhiteSpace(password)) return false;

        var settings = ReadSettings(requireContainer: false, requireServiceAccount: false);
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

    public void SetPassword(string samAccountName, string? userPrincipalName, string newPassword)
    {
        if (!IsEnabled)
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Configuration, "Active Directory n'est pas active.");
        EnsureWindows();

        var settings = ReadSettings(requireContainer: true, requireServiceAccount: true);
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

    public void SetAccountEnabled(string samAccountName, string? userPrincipalName, bool enabled)
    {
        if (!IsEnabled)
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Configuration, "Active Directory n'est pas active.");
        EnsureWindows();

        var settings = ReadSettings(requireContainer: true, requireServiceAccount: true);
        if (!enabled && string.IsNullOrWhiteSpace(settings.DisabledUsersContainerDistinguishedName))
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Configuration, "ActiveDirectory:DisabledUsersContainerDistinguishedName est requis pour desactiver un compte.");

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

    private PrincipalContext CreateUserContainerContext(ActiveDirectorySettings settings)
    {
        var serverOrDomain = !string.IsNullOrWhiteSpace(settings.DomainController)
            ? settings.DomainController
            : settings.DomainDnsName;

        return new PrincipalContext(
            ContextType.Domain,
            serverOrDomain,
            settings.UsersContainerDistinguishedName,
            ContextOptions.Negotiate,
            settings.ServiceAccountUser,
            settings.ServiceAccountPassword);
    }

    private PrincipalContext CreateDomainContext(ActiveDirectorySettings settings, bool useServiceAccount)
    {
        var serverOrDomain = !string.IsNullOrWhiteSpace(settings.DomainController)
            ? settings.DomainController
            : settings.DomainDnsName;

        if (useServiceAccount)
            return new PrincipalContext(ContextType.Domain, serverOrDomain, null, ContextOptions.Negotiate, settings.ServiceAccountUser, settings.ServiceAccountPassword);

        return new PrincipalContext(ContextType.Domain, serverOrDomain);
    }

    private static void MoveUserToContainer(UserPrincipal user, ActiveDirectorySettings settings, string targetContainerDn)
    {
        if (string.IsNullOrWhiteSpace(targetContainerDn)) return;

        using var entry = (DirectoryEntry)user.GetUnderlyingObject();
        var currentDn = entry.Properties["distinguishedName"]?.Value?.ToString() ?? string.Empty;
        if (currentDn.EndsWith(targetContainerDn, StringComparison.OrdinalIgnoreCase))
            return;

        var serverOrDomain = !string.IsNullOrWhiteSpace(settings.DomainController)
            ? settings.DomainController
            : settings.DomainDnsName;
        using var target = new DirectoryEntry($"LDAP://{serverOrDomain}/{targetContainerDn}", settings.ServiceAccountUser, settings.ServiceAccountPassword);
        entry.MoveTo(target);
        entry.CommitChanges();
    }

    private ActiveDirectorySettings ReadSettings(bool requireContainer, bool requireServiceAccount)
    {
        var section = _configuration.GetSection("ActiveDirectory");
        var domainDns = (section["DomainDnsName"] ?? string.Empty).Trim();
        var netBios = (section["NetBiosName"] ?? string.Empty).Trim();
        var controller = (section["DomainController"] ?? string.Empty).Trim();
        var container = (section["UsersContainerDistinguishedName"] ?? string.Empty).Trim();
        var disabledContainer = (section["DisabledUsersContainerDistinguishedName"] ?? string.Empty).Trim();
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

        return new ActiveDirectorySettings(domainDns, netBios, controller, container, disabledContainer, serviceUser, servicePassword);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new ActiveDirectoryOperationException(ActiveDirectoryErrorKind.Configuration, "System.DirectoryServices.AccountManagement est disponible uniquement sur Windows.");
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

    private sealed record ActiveDirectorySettings(
        string DomainDnsName,
        string NetBiosName,
        string DomainController,
        string UsersContainerDistinguishedName,
        string DisabledUsersContainerDistinguishedName,
        string ServiceAccountUser,
        string ServiceAccountPassword);
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
