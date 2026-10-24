using System.Text.Json;
using Microsoft.Win32;
using GameProfileManager.Core;

namespace GameProfileManager.Infrastructure;

internal sealed class RegistryValueEnvelope
{
    public bool Exists { get; set; }
    public string Kind { get; set; } = "";
    public string? StringValue { get; set; }
    public long? NumberValue { get; set; }
    public string[]? StringValues { get; set; }
    public string? BinaryBase64 { get; set; }
}

/// <summary>Handles one explicitly selected value under HKCU\Software; never creates or deletes keys.</summary>
public static class RegistryGameConfigBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static bool IsAllowed(GameConfigSource source, out string reason)
    {
        reason = "";
        if (source.Kind != GameConfigSourceKind.Registry || !source.TargetPath.StartsWith("HKCU\\Software\\", StringComparison.OrdinalIgnoreCase))
        { reason = "Поддерживаются только значения HKEY_CURRENT_USER\\Software."; return false; }
        if (string.IsNullOrWhiteSpace(source.RegistryValueName) || source.RegistryValueName.Contains('\\'))
        { reason = "Укажите точное имя значения реестра."; return false; }
        string subKey = source.TargetPath["HKCU\\".Length..];
        if (source.TargetPath.Contains('/') || subKey.Split('\\').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
        { reason = "Недопустимый путь раздела реестра."; return false; }
        return true;
    }

    public static byte[] Read(GameConfigSource source)
    {
        if (!IsAllowed(source, out string reason)) throw new InvalidOperationException(reason);
        string subKey = source.TargetPath["HKCU\\".Length..];
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        if (key is null) return Serialize(new RegistryValueEnvelope());
        object? value = key.GetValue(source.RegistryValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return Serialize(new RegistryValueEnvelope());
        RegistryValueKind kind = key.GetValueKind(source.RegistryValueName);
        var envelope = new RegistryValueEnvelope { Exists = true, Kind = kind.ToString() };
        switch (kind)
        {
            case RegistryValueKind.String: case RegistryValueKind.ExpandString:
                envelope.StringValue = (string)value; break;
            case RegistryValueKind.DWord: envelope.NumberValue = Convert.ToInt32(value); break;
            case RegistryValueKind.QWord: envelope.NumberValue = Convert.ToInt64(value); break;
            case RegistryValueKind.MultiString: envelope.StringValues = (string[])value; break;
            case RegistryValueKind.Binary: envelope.BinaryBase64 = Convert.ToBase64String((byte[])value); break;
            default: throw new InvalidDataException("Тип значения реестра не поддерживается.");
        }
        return Serialize(envelope);
    }

    public static void Write(GameConfigSource source, byte[] data)
    {
        if (!IsAllowed(source, out string reason)) throw new InvalidOperationException(reason);
        if (data.Length > GameProfileManager.Application.GameConfigPathSafety.MaxFileBytes)
            throw new InvalidDataException("Значение реестра превышает ограничение 32 МБ.");
        var envelope = Deserialize(data);
        string subKey = source.TargetPath["HKCU\\".Length..];
        if (!envelope.Exists)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
            existing?.DeleteValue(source.RegistryValueName, throwOnMissingValue: false);
            return;
        }
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        if (key is null)
        {
            if (!envelope.Exists) return;
            throw new IOException("Выбранный раздел HKCU\\Software больше не существует.");
        }
        if (!Enum.TryParse(envelope.Kind, out RegistryValueKind kind) || kind is RegistryValueKind.Unknown or RegistryValueKind.None)
            throw new InvalidDataException("В снимке указан недопустимый тип реестра.");
        object value = kind switch
        {
            RegistryValueKind.String => envelope.StringValue ?? "",
            RegistryValueKind.ExpandString => envelope.StringValue ?? "",
            RegistryValueKind.DWord => checked((int)(envelope.NumberValue ?? 0)),
            RegistryValueKind.QWord => envelope.NumberValue ?? 0,
            RegistryValueKind.MultiString => envelope.StringValues ?? [],
            RegistryValueKind.Binary => Convert.FromBase64String(envelope.BinaryBase64 ?? ""),
            _ => throw new InvalidDataException("Тип значения реестра не разрешён.")
        };
        key.SetValue(source.RegistryValueName, value, kind);
    }

    public static string Kind(byte[] data) => Deserialize(data).Kind;
    private static byte[] Serialize(RegistryValueEnvelope value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > GameProfileManager.Application.GameConfigPathSafety.MaxFileBytes)
            throw new InvalidDataException("Значение реестра превышает ограничение 32 МБ.");
        return bytes;
    }
    private static RegistryValueEnvelope Deserialize(byte[] value) =>
        JsonSerializer.Deserialize<RegistryValueEnvelope>(value, JsonOptions) ?? new RegistryValueEnvelope();
}
