using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsvHelper;
using OmniToolbox.Data;

var jsonOptions = new JsonSerializerOptions { WriteIndented = true, MaxDepth = 64 };
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};
try
{
    if (args.Length < 1 || args[0] is not ("generate" or "verify" or "export") || args.Length % 2 != 1)
        throw new ArgumentException("Usage: generate --data-directory <Data> --output-directory <packages>; verify --data-directory <Data> --package-directory <packages>; export --package-directory <packages> --output-directory <exports>.");
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 1; index < args.Length; index += 2)
        if (!options.TryAdd(args[index], Path.GetFullPath(args[index + 1])))
            throw new ArgumentException($"Repeated option: {args[index]}.");
    var mode = args[0];
    var outputDirectory = options.GetValueOrDefault("--output-directory", string.Empty);
    if (mode is "generate" or "export" && outputDirectory.Length == 0)
        throw new ArgumentException("Output directory is required.");
    var packageDirectory = mode == "generate" ? outputDirectory : options.GetValueOrDefault("--package-directory");
    if (packageDirectory is null)
        throw new ArgumentException("Package directory is required.");
    GameDataPackageIndex packageIndex;
    Dictionary<string, string> files = new(StringComparer.Ordinal);
    Dictionary<string, byte[]> resourceBytes = new(StringComparer.Ordinal);
    if (mode is "generate" or "verify")
    {
        if (!options.TryGetValue("--data-directory", out var dataDirectory))
            throw new ArgumentException("Data directory is required.");
        var loaded = ReadResources(dataDirectory);
        packageIndex = loaded.Index;
        files = loaded.Files;
        resourceBytes = loaded.Bytes;
    }
    else
    {
        packageIndex = ReadIndex(packageDirectory);
    }
    foreach (var (kind, inputs) in new[]
    {
        ("item-catalog", GameDataPackageBuilder.ItemCatalogInputs),
        ("item-sources", GameDataPackageBuilder.WikiInputs),
        ("dungeon-sources", GameDataPackageBuilder.DungeonInputs)
    })
    {
        cancellation.Token.ThrowIfCancellationRequested();
        var name = kind + ".msgpack";
        var packagePath = Path.Combine(packageDirectory, name);
        object? expected = mode is "generate" or "verify" ? kind switch
        {
            "item-catalog" => GameDataPackageBuilder.BuildItemCatalog(files, cancellation.Token),
            "item-sources" => GameDataPackageBuilder.BuildWikiItems(files["WikiItemSources.json"], cancellation.Token),
            "dungeon-sources" => GameDataPackageBuilder.BuildDungeonSources(files, cancellation.Token),
            _ => throw new InvalidDataException("Unsupported runtime package.")
        } : null;
        byte[] bytes;
        if (mode == "generate")
        {
            bytes = expected switch
            {
                ItemCatalogData data => GameDataPackageCodec.Serialize(data, cancellation.Token),
                WikiItemData data => GameDataPackageCodec.Serialize(data, cancellation.Token),
                DungeonSourceData data => GameDataPackageCodec.Serialize(data, cancellation.Token),
                _ => throw new InvalidDataException("Missing runtime package content.")
            };
            packageIndex.Packages.Add(kind, new()
            {
                Kind = kind, Name = name, SHA256 = Digest(bytes), InputHash = HashInputs(inputs, resourceBytes), Inputs = inputs
            });
        }
        else
        {
            bytes = File.ReadAllBytes(packagePath);
            var publishedIndex = mode == "verify" ? ReadIndex(packageDirectory) : packageIndex;
            if (!publishedIndex.Packages.TryGetValue(kind, out var info) || info.Kind != kind || info.Name != name ||
                info.SHA256 != Digest(bytes) || !info.Inputs.SequenceEqual(inputs, StringComparer.Ordinal) ||
                mode != "export" && info.InputHash != HashInputs(inputs, resourceBytes))
                throw new InvalidDataException($"Runtime package metadata mismatch: {kind}.");
            if (mode == "verify")
                packageIndex.Packages.Add(kind, info);
        }
        object decoded = kind switch
        {
            "item-catalog" => GameDataPackageCodec.Deserialize<ItemCatalogData>(bytes, cancellation.Token),
            "item-sources" => GameDataPackageCodec.Deserialize<WikiItemData>(bytes, cancellation.Token),
            "dungeon-sources" => GameDataPackageCodec.Deserialize<DungeonSourceData>(bytes, cancellation.Token),
            _ => throw new InvalidDataException("Unsupported runtime package.")
        };
        if (expected is not null && !JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(expected, expected.GetType()),
                JsonSerializer.SerializeToElement(decoded, decoded.GetType())))
            throw new InvalidDataException($"Runtime package round-trip changed fields: {kind}.");
        if (mode == "generate")
            WriteAtomic(packagePath, bytes);
        if (mode == "export")
        {
            WriteAtomic(Path.Combine(outputDirectory, kind + ".json"), JsonSerializer.SerializeToUtf8Bytes(decoded, decoded.GetType(), jsonOptions));
        }
        Console.WriteLine($"{kind}: {bytes.Length} bytes, SHA256={Digest(bytes)}, status=verified");
    }
    var indexBytes = JsonSerializer.SerializeToUtf8Bytes(packageIndex, jsonOptions);
    if (mode == "generate")
        WriteAtomic(Path.Combine(packageDirectory, "embedded-index.json"), indexBytes);
    if (mode == "verify")
    {
        var published = ReadIndex(packageDirectory);
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(packageIndex), JsonSerializer.SerializeToElement(published)))
            throw new InvalidDataException("Runtime package index does not match its source inputs.");
    }
    if (mode == "export")
        WriteAtomic(Path.Combine(outputDirectory, "embedded-index.json"), indexBytes);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Game data operation cancelled.");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Game data operation failed: {exception.GetType().Name}: {exception.Message}");
    return 1;
}

(GameDataPackageIndex Index, Dictionary<string, string> Files, Dictionary<string, byte[]> Bytes) ReadResources(string directory)
{
    using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "resource-manifest.json")));
    if (manifest.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
        throw new InvalidDataException("Unsupported resource manifest version.");
    var result = new GameDataPackageIndex();
    var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
    var bytesByName = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var resource in manifest.RootElement.GetProperty("resources").EnumerateArray())
    {
        cancellation.Token.ThrowIfCancellationRequested();
        var name = resource.GetProperty("name").GetString() ?? throw new InvalidDataException("Resource name is missing.");
        var relative = resource.GetProperty("path").GetString() ?? throw new InvalidDataException("Resource path is missing.");
        var path = Path.GetFullPath(relative, directory);
        if (Path.GetFileName(name) != name || !path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid resource path: {name}.");
        var bytes = File.ReadAllBytes(path);
        var content = File.ReadAllText(path);
        if (!bytesByName.TryAdd(name, bytes) || !loaded.TryAdd(name, content))
            throw new InvalidDataException($"Duplicate resource: {name}.");
        string[]? header = null;
        string? generatedAtUTC = null;
        if (name.EndsWith(".csv", StringComparison.Ordinal))
        {
            using var text = new StringReader(content);
            using var csv = new CsvReader(text, CultureInfo.InvariantCulture);
            if (!csv.Read())
                throw new InvalidDataException($"CSV header is missing: {name}.");
            csv.ReadHeader();
            header = csv.HeaderRecord;
        }
        else if (name.EndsWith(".json", StringComparison.Ordinal))
        {
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.TryGetProperty("generatedAtUtc", out var stamp))
                generatedAtUTC = stamp.GetString();
        }
        result.Resources.Add(name, new()
        {
            Name = name, Path = relative, Group = resource.GetProperty("group").GetString() ?? string.Empty,
            SHA256 = Digest(bytes), ByteLength = bytes.LongLength, Header = header, GeneratedAtUTC = generatedAtUTC
        });
    }
    foreach (var group in new[] { "Supplemental", "Community" })
        result.Groups.Add(group, HashInputs(result.Resources.Values.Where(resource => resource.Group == group).Select(resource => resource.Name), bytesByName));
    return (result, loaded, bytesByName);
}

static GameDataPackageIndex ReadIndex(string directory)
{
    var index = JsonSerializer.Deserialize<GameDataPackageIndex>(File.ReadAllBytes(Path.Combine(directory, "embedded-index.json")));
    return index is { SchemaVersion: 1, GeneratorVersion: 1 } ? index : throw new InvalidDataException("Unsupported runtime package index.");
}

static string HashInputs(IEnumerable<string> names, IReadOnlyDictionary<string, byte[]> bytes)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var name in names)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(name));
        hash.AppendData(bytes[name]);
    }
    return Convert.ToHexString(hash.GetHashAndReset());
}

static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

static void WriteAtomic(string path, byte[] bytes)
{
    if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        return;
    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidDataException("Output directory is missing."));
    var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
    try
    {
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, true);
    }
    finally
    {
        if (File.Exists(temporary))
            File.Delete(temporary);
    }
}
