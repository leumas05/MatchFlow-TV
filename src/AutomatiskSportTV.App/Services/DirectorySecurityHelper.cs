using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AutomatiskSportTV.App.Services;

/// <summary>
/// Restricts local configuration and WebView2 profile folders to the current Windows user account via NTFS ACLs.
/// </summary>
public static class DirectorySecurityHelper
{
    public static void RestrictDirectoryToCurrentUser(string directoryPath)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null)
            {
                return;
            }

            var dirInfo = new DirectoryInfo(directoryPath);
            var security = dirInfo.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var existingRules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in existingRules)
            {
                security.RemoveAccessRuleSpecific(rule);
            }

            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            dirInfo.SetAccessControl(security);
        }
        catch
        {
            // Non-NTFS or restricted environment: fall back cleanly without breaking app startup
        }
    }
}
