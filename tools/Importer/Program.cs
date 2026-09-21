using System;
using System.IO;
using Klippy.Services;

// One-off loader: merges a Klippy-format JSON file into the local store using the
// same Merge path the in-app importer uses, so external-identity matching,
// normalisation and the atomic save all behave identically.
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: Importer <json-path>");
    return 1;
}

await using var file = File.OpenRead(args[0]);
var parsed = SnippetStore.TryParseSnippets(file);
if (parsed is null)
{
    Console.Error.WriteLine("not a valid Klippy JSON file");
    return 1;
}

// The app honours the configured file locations before it opens anything; so must this,
// or a re-import would quietly land in the folder — or the file — the user moved away from.
if (!StorageLocations.TryApply(AppSettings.Current, out var problem))
    Console.Error.WriteLine($"Ignoring \"DataDirectory\": {problem}.");

var store = new SnippetStore();
Console.WriteLine($"store    : {store.FilePath}");
Console.WriteLine($"before   : {store.Count} snippets");

var (added, updated) = store.Merge(parsed);

Console.WriteLine($"incoming : {parsed.Count}");
Console.WriteLine($"added    : {added}");
Console.WriteLine($"updated  : {updated}");
Console.WriteLine($"after    : {store.Count} snippets");
return 0;
