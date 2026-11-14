using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

#nullable enable

namespace RemotePartyFinderReborn;

internal static class DpapiSecretProtector {
    private const string ProtectedPrefix = "dpapi:v1:";
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("RemotePartyFinderReborn.ConfigSecrets.v1");

    [ThreadStatic]
    private static int _deserializationDepth;

    [ThreadStatic]
    private static bool _plaintextDetectedDuringDeserialization;

    internal static bool IsProtected(string? value) {
        return !string.IsNullOrEmpty(value) && value.StartsWith(ProtectedPrefix, StringComparison.Ordinal);
    }

    internal static void BeginDeserialization() {
        _deserializationDepth++;
        if (_deserializationDepth == 1) {
            _plaintextDetectedDuringDeserialization = false;
        }
    }

    internal static bool EndDeserialization() {
        if (_deserializationDepth <= 0) {
            _deserializationDepth = 0;
            _plaintextDetectedDuringDeserialization = false;
            return false;
        }

        _deserializationDepth--;
        if (_deserializationDepth > 0) {
            return false;
        }

        var detected = _plaintextDetectedDuringDeserialization;
        _plaintextDetectedDuringDeserialization = false;
        return detected;
    }

    internal static void MarkPlaintextDetected() {
        if (_deserializationDepth > 0) {
            _plaintextDetectedDuringDeserialization = true;
        }
    }

    internal static string Protect(string? plaintext) {
        if (string.IsNullOrEmpty(plaintext)) {
            return string.Empty;
        }

        if (!OperatingSystem.IsWindows()) {
            return plaintext;
        }

        try {
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var protectedBytes = ProtectedData.Protect(plaintextBytes, OptionalEntropy, DataProtectionScope.CurrentUser);
            return string.Concat(ProtectedPrefix, Convert.ToBase64String(protectedBytes));
        } catch (CryptographicException ex) {
            TryLogWarning($"Failed to protect configuration secret with DPAPI: {ex.Message}");
            return plaintext;
        } catch (PlatformNotSupportedException ex) {
            TryLogWarning($"DPAPI secret protection is not supported on this platform: {ex.Message}");
            return plaintext;
        }
    }

    internal static string Unprotect(string? stored, out bool needsMigration) {
        needsMigration = false;
        if (string.IsNullOrEmpty(stored)) {
            return string.Empty;
        }

        if (!IsProtected(stored)) {
            if (OperatingSystem.IsWindows()) {
                needsMigration = true;
            }
            return stored;
        }

        var encodedPayload = stored[ProtectedPrefix.Length..];
        if (!OperatingSystem.IsWindows()) {
            TryLogWarning("Encountered DPAPI-protected configuration secret on a non-Windows platform.");
            return string.Empty;
        }

        try {
            var protectedBytes = Convert.FromBase64String(encodedPayload);
            var plaintextBytes = ProtectedData.Unprotect(protectedBytes, OptionalEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintextBytes);
        } catch (FormatException ex) {
            TryLogWarning($"Invalid base64 payload in DPAPI-protected configuration secret: {ex.Message}");
            return string.Empty;
        } catch (CryptographicException ex) {
            TryLogWarning($"Failed to unprotect DPAPI configuration secret: {ex.Message}");
            return string.Empty;
        } catch (PlatformNotSupportedException ex) {
            TryLogWarning($"DPAPI secret unprotection is not supported on this platform: {ex.Message}");
            return string.Empty;
        }
    }

    private static void TryLogWarning(string message) {
        try {
            Plugin.Log?.Warning(message);
        } catch {
            // Tests may exercise DPAPI helpers without an initialized Dalamud Plugin instance.
        }
    }
}

internal sealed class DpapiProtectedStringJsonConverter : JsonConverter<string?> {
    public override string ReadJson(
        JsonReader reader,
        Type objectType,
        string? existingValue,
        bool hasExistingValue,
        JsonSerializer serializer
    ) {
        if (reader.TokenType == JsonToken.Null) {
            return string.Empty;
        }

        if (reader.TokenType != JsonToken.String) {
            return reader.Value?.ToString() ?? string.Empty;
        }

        var rawValue = reader.Value as string ?? string.Empty;
        var plaintext = DpapiSecretProtector.Unprotect(rawValue, out var needsMigration);
        if (needsMigration) {
            DpapiSecretProtector.MarkPlaintextDetected();
        }
        return plaintext;
    }

    public override void WriteJson(JsonWriter writer, string? value, JsonSerializer serializer) {
        if (string.IsNullOrEmpty(value)) {
            writer.WriteValue(string.Empty);
            return;
        }

        writer.WriteValue(DpapiSecretProtector.Protect(value));
    }
}
