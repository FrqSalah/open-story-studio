using System.CommandLine;
using System.Globalization;
using Studio.Core;
using Studio.Core.Validation;
using Studio.Device;
using Studio.Device.Storage;
using Studio.Formats.Archive;
using Studio.Formats.DeviceFs;
using Studio.Formats.Library;
using Studio.Formats.Raw;
using Studio.Media;
using Studio.Media.Audio;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var deviceOption = new Option<DirectoryInfo?>("--device", "-d")
{
    Description = "Dossier racine de l'appareil (ex. E:\\). Détecté automatiquement si absent.",
    Recursive = true,
};
var experimentalOption = new Option<bool>("--experimental")
{
    Description = "Autorise l'écriture sur les appareils v1 et v3 (non validée sur du vrai matériel).",
    Recursive = true,
};
var packArgument = new Argument<FileInfo>("pack") { Description = "Archive de pack (.zip, format STUdio)." };
var idArgument = new Argument<Guid>("id") { Description = "Identifiant du pack." };

var root = new RootCommand("Open Story Studio — gestion des packs d'histoires pour conteuses.");
root.Options.Add(deviceOption);
root.Options.Add(experimentalOption);

// ----- device -----
var device = new Command("device", "Opérations sur l'appareil connecté.");
root.Subcommands.Add(device);

var info = new Command("info", "Affiche les informations de l'appareil.");
info.SetAction(async (parse, ct) =>
{
    var (_, d) = await OpenDeviceAsync(parse, ct);
    var i = await d.GetInfoAsync(ct);
    Console.WriteLine($"Génération   : {i.Generation}");
    Console.WriteLine($"Firmware     : {i.Firmware}");
    Console.WriteLine($"N° de série  : {i.SerialNumber ?? "inconnu"}");
    Console.WriteLine($"Emplacement  : {i.Location}");
    if (i.TotalBytes is { } total) Console.WriteLine($"Espace       : {Mb(i.FreeBytes ?? 0)} libres sur {Mb(total)}");
    Console.WriteLine($"Écriture     : {(i.IsWriteSupported ? "oui" : "désactivée (utilisez --experimental)")}");
    return 0;
});
device.Subcommands.Add(info);

var list = new Command("list", "Liste les packs installés, dans l'ordre de l'appareil.");
list.SetAction(async (parse, ct) =>
{
    var (_, d) = await OpenDeviceAsync(parse, ct);
    var library = await LibraryTitlesAsync(ct);
    var packs = await d.ListPacksAsync(ct);
    for (var n = 0; n < packs.Count; n++)
    {
        var p = packs[n];
        var title = library.GetValueOrDefault(p.Id, "(titre inconnu)");
        Console.WriteLine($"{n + 1,3}. {p.Id}  v{p.Version}  {Mb(p.SizeBytes),9}  {(p.NightMode ? "nuit " : "     ")}{(p.IsValid ? "" : "[CORROMPU] ")}{title}");
    }
    if (packs.Count == 0) Console.WriteLine("Aucun pack.");
    return 0;
});
device.Subcommands.Add(list);

var install = new Command("install", "Convertit et installe un pack.") { packArgument };
install.SetAction(async (parse, ct) =>
{
    var (manager, d) = await OpenDeviceAsync(parse, ct);
    var pack = ReadPack(parse.GetRequiredValue(packArgument));
    var last = -1;
    var progress = new Progress<TransferProgress>(p =>
    {
        var percent = (int)(p.Fraction * 100);
        if (percent / 5 == last / 5) return;
        last = percent;
        Console.Write($"\r{Phase(p.Phase),-14} {percent,3} %");
    });
    await manager.InstallAsync(d, pack, progress, ct);
    Console.WriteLine($"\r« {pack.Title} » installé.            ");
    return 0;
});
device.Subcommands.Add(install);

var remove = new Command("remove", "Supprime un pack de l'appareil.") { idArgument };
remove.SetAction(async (parse, ct) =>
{
    var (manager, d) = await OpenDeviceAsync(parse, ct);
    await manager.BackupAsync(d, ct);
    await d.RemoveAsync(parse.GetRequiredValue(idArgument), ct);
    Console.WriteLine("Pack supprimé.");
    return 0;
});
device.Subcommands.Add(remove);

var idsArgument = new Argument<Guid[]>("ids") { Description = "Tous les identifiants, dans le nouvel ordre.", Arity = ArgumentArity.OneOrMore };
var reorder = new Command("reorder", "Change l'ordre des packs.") { idsArgument };
reorder.SetAction(async (parse, ct) =>
{
    var (manager, d) = await OpenDeviceAsync(parse, ct);
    await manager.BackupAsync(d, ct);
    await d.ReorderAsync(parse.GetRequiredValue(idsArgument), ct);
    Console.WriteLine("Ordre mis à jour.");
    return 0;
});
device.Subcommands.Add(reorder);

var outputArgument = new Argument<FileInfo>("output") { Description = "Fichier .zip à créer." };
var export = new Command("export", "Exporte un pack de l'appareil vers une archive.") { idArgument, outputArgument };
export.SetAction(async (parse, ct) =>
{
    var (_, d) = await OpenDeviceAsync(parse, ct);
    var pack = await d.ExportAsync(parse.GetRequiredValue(idArgument), ct);
    var output = parse.GetRequiredValue(outputArgument);
    await File.WriteAllBytesAsync(output.FullName, ArchivePackFormat.Write(pack), ct);
    Console.WriteLine($"Exporté vers {output.FullName}");
    return 0;
});
device.Subcommands.Add(export);

var backup = new Command("backup", "Sauvegarde l'index de l'appareil.");
backup.SetAction(async (parse, ct) =>
{
    var (manager, d) = await OpenDeviceAsync(parse, ct);
    Console.WriteLine($"Sauvegarde : {await manager.BackupAsync(d, ct)}");
    return 0;
});
device.Subcommands.Add(backup);

// ----- pack -----
var packCommand = new Command("pack", "Opérations sur les archives de packs.");
root.Subcommands.Add(packCommand);

var validate = new Command("validate", "Vérifie la structure d'un pack.") { packArgument };
validate.SetAction(parse =>
{
    var pack = ReadPack(parse.GetRequiredValue(packArgument));
    var issues = PackValidator.Validate(pack);
    foreach (var issue in issues) Console.WriteLine($"[{(issue.Severity == IssueSeverity.Error ? "ERREUR" : "attention")}] {issue.Message}");
    Console.WriteLine(issues.Count == 0 ? "Aucun problème." : $"{issues.Count} remarque(s).");
    return issues.Any(i => i.Severity == IssueSeverity.Error) ? 1 : 0;
});
packCommand.Subcommands.Add(validate);

var formatOption = new Option<string>("--format", "-f") { Description = "fs (v2/v3) ou raw (v1).", DefaultValueFactory = _ => "fs" };
formatOption.AcceptOnlyFromAmong("fs", "raw");
var outOption = new Option<DirectoryInfo>("--out", "-o") { Description = "Dossier de sortie.", Required = true };
var convert = new Command("convert", "Convertit un pack au format appareil (fichiers en clair, pour inspection).") { packArgument, formatOption, outOption };
convert.SetAction(async (parse, ct) =>
{
    var pack = ReadPack(parse.GetRequiredValue(packArgument));
    var raw = parse.GetValue(formatOption) == "raw";
    var preparer = new DeviceAssetPreparer(FfmpegAudioTranscoder.CreateDefault());
    var ready = await preparer.PrepareAsync(pack, raw ? DeviceGeneration.V1 : DeviceGeneration.V2, cancellationToken: ct);
    var output = parse.GetRequiredValue(outOption);
    output.Create();
    if (raw)
    {
        var path = Path.Combine(output.FullName, $"{pack.Id}.pack");
        await File.WriteAllBytesAsync(path, RawPackFormat.Write(ready), ct);
        Console.WriteLine($"Image v1 écrite : {path}");
    }
    else
    {
        var folder = Path.Combine(output.FullName, PackIndex.FolderName(pack.Id));
        foreach (var (file, data) in FsPackFormat.Write(ready))
        {
            var target = Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, data, ct);
        }
        Console.WriteLine($"Dossier FS écrit (non chiffré) : {folder}");
    }
    return 0;
});
packCommand.Subcommands.Add(convert);

var titleArgument = new Argument<string>("title") { Description = "Titre du pack." };
var storiesOption = new Option<int>("--stories", "-n") { Description = "Nombre d'histoires du menu.", DefaultValueFactory = _ => 2 };
var create = new Command("new", "Crée un pack vide à partir d'un modèle.") { titleArgument, outputArgument, storiesOption };
create.SetAction(async (parse, ct) =>
{
    var pack = PackTemplates.StoryMenu(parse.GetRequiredValue(titleArgument), parse.GetValue(storiesOption));
    var output = parse.GetRequiredValue(outputArgument);
    await File.WriteAllBytesAsync(output.FullName, ArchivePackFormat.Write(pack), ct);
    Console.WriteLine($"Pack créé : {output.FullName} ({pack.Id})");
    return 0;
});
packCommand.Subcommands.Add(create);

// ----- library -----
var libraryCommand = new Command("library", "Bibliothèque locale (partagée avec l'application).");
root.Subcommands.Add(libraryCommand);

var libraryList = new Command("list", "Liste les packs de la bibliothèque.");
libraryList.SetAction(async (_, ct) =>
{
    var library = new FolderPackLibrary(FolderPackLibrary.DefaultFolder);
    foreach (var e in await library.ListAsync(ct)) Console.WriteLine($"{e.Id}  {Mb(e.SizeBytes),9}  {e.Title}");
    return 0;
});
libraryCommand.Subcommands.Add(libraryList);

var libraryImport = new Command("import", "Importe une archive dans la bibliothèque.") { packArgument };
libraryImport.SetAction(async (parse, ct) =>
{
    var library = new FolderPackLibrary(FolderPackLibrary.DefaultFolder);
    await using var stream = parse.GetRequiredValue(packArgument).OpenRead();
    var pack = await library.ImportAsync(stream, ct);
    Console.WriteLine($"Importé : {pack.Title} ({pack.Id})");
    return 0;
});
libraryCommand.Subcommands.Add(libraryImport);

try
{
    return await root.Parse(args).InvokeAsync();
}
catch (Exception e) when (e is DeviceException or MediaConversionException or InvalidDataException or IOException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Erreur : {e.Message}");
    return 2;
}

async Task<(DeviceManager Manager, IStoryDevice Device)> OpenDeviceAsync(ParseResult parse, CancellationToken ct)
{
    var manager = new DeviceManager(
        new DeviceAssetPreparer(FfmpegAudioTranscoder.CreateDefault()),
        new DeviceManagerOptions { AllowExperimentalWrites = parse.GetValue(experimentalOption) });

    var folder = parse.GetValue(deviceOption);
    var found = folder is null
        ? await manager.DetectAsync(ct)
        : await manager.OpenAsync(new LocalDeviceStorage(folder.FullName), ct);
    return (manager, found ?? throw new DeviceException(folder is null
        ? "Aucun appareil détecté. Branchez-le, ou indiquez son dossier avec --device."
        : $"{folder.FullName} n'est pas un appareil reconnu (fichier .md absent)."));
}

static Studio.Core.Model.StoryPack ReadPack(FileInfo file)
{
    using var stream = file.OpenRead();
    return ArchivePackFormat.Read(stream);
}

static async Task<Dictionary<Guid, string>> LibraryTitlesAsync(CancellationToken ct)
{
    var library = new FolderPackLibrary(FolderPackLibrary.DefaultFolder);
    return (await library.ListAsync(ct)).ToDictionary(e => e.Id, e => e.Title);
}

static string Mb(long bytes) => (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.CurrentCulture) + " Mo";

static string Phase(TransferPhase phase) => phase switch
{
    TransferPhase.Converting => "Conversion",
    TransferPhase.BackingUp => "Sauvegarde",
    TransferPhase.Writing => "Copie",
    TransferPhase.Verifying => "Vérification",
    TransferPhase.Committing => "Finalisation",
    _ => "Terminé",
};
