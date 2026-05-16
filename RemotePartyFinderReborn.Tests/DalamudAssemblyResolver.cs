using System.Reflection;
using System.Threading;

namespace RemotePartyFinderReborn.Tests;

internal static class DalamudAssemblyResolver {
    private static int registered;

    internal static void Register() {
        if (Interlocked.Exchange(ref registered, 1) != 0) {
            return;
        }

        AppDomain.CurrentDomain.AssemblyResolve += static (_, args) => {
            var assemblyName = new AssemblyName(args.Name).Name;
            if (string.IsNullOrWhiteSpace(assemblyName)) {
                return null;
            }

            var dalamudHome = Environment.GetEnvironmentVariable("DALAMUD_HOME");
            if (string.IsNullOrWhiteSpace(dalamudHome)) {
                dalamudHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "XIVLauncher",
                    "addon",
                    "Hooks",
                    "dev"
                );
            }

            var candidatePath = Path.Combine(dalamudHome, assemblyName + ".dll");
            return File.Exists(candidatePath) ? Assembly.LoadFrom(candidatePath) : null;
        };
    }
}
