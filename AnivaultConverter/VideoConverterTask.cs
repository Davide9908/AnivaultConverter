using System.Globalization;
using System.Text;
using Coravel.Invocable;
using FFMpegCore;
using FFMpegCore.Arguments;
using FFMpegCore.Enums;

namespace AnivaultConverter;

public class VideoConverterTask : IInvocable, ICancellableInvocable
{
    private const string DownloadingPrefix = "downloading_";
    private const string H264Codec = "h264";
    private readonly string[] _availableExtensions = [".mp4", ".mkv"];
    private readonly string _downloadingFolderPath;
    private readonly ILogger<VideoConverterTask> _log;
    private readonly string _toWatchFolderPath;

    public VideoConverterTask(IConfiguration configuration, ILogger<VideoConverterTask> log)
    {
        _downloadingFolderPath = configuration["DownloadingFolderPath"] ??
                                 throw new InvalidOperationException("DownloadingFolderPath is missing");
        _toWatchFolderPath = configuration["ToWatchFolderPath"] ??
                             throw new InvalidOperationException("ToWatchFolderPath is missing");
        _log = log;
    }

    public CancellationToken CancellationToken { get; set; }

    public async Task Invoke()
    {
        var directoryInfo = new DirectoryInfo(_downloadingFolderPath);
        var fileToConvertList = directoryInfo.GetFiles()
            .Where(fi => _availableExtensions.Contains(fi.Extension) && !fi.Name.StartsWith(DownloadingPrefix))
            .ToList();
        List<Task> runningConversion = [];
        foreach (FileInfo fileToConvert in fileToConvertList)
        {
            if (runningConversion.Count(t => !t.IsCompleted) >= 2)
            {
                await Task.WhenAny(runningConversion);
                CancellationToken.ThrowIfCancellationRequested();
            }

            try
            {
                IMediaAnalysis mediaInfo =
                    await FFProbe.AnalyseAsync(fileToConvert.FullName, cancellationToken: CancellationToken);
                var subs = mediaInfo.SubtitleStreams.Where(s => s.Language == "ita").ToList();
                if (mediaInfo.VideoStreams.First().CodecName != H264Codec && !subs.Any())
                {
                    File.Move(fileToConvert.FullName, Path.Combine(_toWatchFolderPath, fileToConvert.Name));
                    continue;
                }

                if (!subs.Any())
                {
                    runningConversion.Add(ConvertVideo(fileToConvert));
                    DeleteFile(fileToConvert);
                    return;
                }

                if (subs.Count == 1)
                {
                    var subsIndex = mediaInfo.SubtitleStreams.IndexOf(subs.First());
                    runningConversion.Add(ConvertAndPrintSubs(fileToConvert, subsIndex));
                }
                else
                {
                    runningConversion.Add(ConvertAndPrintSubs(fileToConvert, subs, mediaInfo.SubtitleStreams));
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error processing file {file}", fileToConvert.Name);
            }
        }

        //if i have just 2 files, the foreach ends without waiting for the 2 task, with this i'm sure i wait for everything to complete  
        await Task.WhenAll(runningConversion);
    }

    private async Task ConvertAndPrintSubs(FileInfo fileToConvert, int subIndex)
    {
        try
        {
            var subtitleOptions = SubtitleHardBurnOptions.Create(fileToConvert.FullName);
            subtitleOptions.SetSubtitleIndex(subIndex);
            _log.Info("Inizio la conversione e stampaggio dei sottotitoli del file {filename}", fileToConvert.Name);

            await FFMpegArguments
                // -i "filepath"
                .FromFileInput(fileToConvert.FullName, true, options => options
                    .WithHardwareAcceleration(HardwareAccelerationDevice.QSV)
                    .WithCustomArgument("-hwaccel_output_format nv12")
                    .WithCustomArgument("-c:v h264_qsv")
                )
                .OutputToFile(Path.Combine(_toWatchFolderPath, fileToConvert.Name), true, options => options
                    .WithCustomArgument("-map 0:v:0")
                    .WithCustomArgument("-map 0:a:0")
                    .WithVideoCodec("hevc_qsv")
                    .WithCustomArgument("-global_quality 18")
                    .WithSpeedPreset(Speed.Slow)
                    .WithVideoFilters(filters => filters
                        .HardBurnSubtitle(subtitleOptions)
                    )

                    // -c:a copy
                    .WithAudioCodec(AudioCodec.Copy)
                )
                .NotifyOnError(Console.WriteLine)
                .CancellableThrough(CancellationToken)
                .ProcessAsynchronously();

            _log.Info("Conversione e stampaggio dei sottotitoli del file {filename} completata", fileToConvert.Name);
            DeleteFile(fileToConvert);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error processing file {file}", fileToConvert.Name);
        }
    }
    
    private async Task ConvertAndPrintSubs(FileInfo fileToConvert, List<SubtitleStream> itaSubs, List<SubtitleStream> allSubs)
    {
        try
        {
            _log.Info("Inizio l'estrazione dei sottotitoli del file {filename}", fileToConvert.Name);
            
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "anivaultConverter"));
            
            int dotIndex = fileToConvert.Name.LastIndexOf('.');
            string filename =  fileToConvert.Name.Substring(0, dotIndex);
            List<string> subFilenames = new List<string>(itaSubs.Count);
            foreach (var sub in itaSubs)
            {
                int index = allSubs.IndexOf(sub);
                string subFilename = Path.Combine(Path.GetTempPath(), "anivaultConverter", $"{filename}_sub{index:00}.ass");
                subFilenames.Add(subFilename);
                await ExtractSubtitleTrack(fileToConvert.FullName, $"0:s:{index}", subFilename);
            }

            string combinedSubs = Path.Combine(Path.GetTempPath(), "anivaultConverter", $"{filename}_subCombined.ass");
            await CombineAssSubtitles(subFilenames, combinedSubs);
            
            var burnOptions = SubtitleHardBurnOptions.Create(combinedSubs);
            _log.Info("Inizio la conversione e stampaggio dei sottotitoli del file {filename}", fileToConvert.Name);
            
            await FFMpegArguments
                // -i "filepath"
                .FromFileInput(fileToConvert.FullName)
                .OutputToFile(Path.Combine(_toWatchFolderPath, fileToConvert.Name), true, options => options
                    .WithCustomArgument("-map 0:v:0")
                    .WithCustomArgument("-map 0:a:0")
                    .WithVideoCodec("hevc_qsv")
                    .WithCustomArgument("-global_quality 18")
                    .WithSpeedPreset(Speed.Slow)
                    .WithVideoFilters(filters => filters
                        .HardBurnSubtitle(burnOptions)
                    )
                    // -c:a copy
                    .WithAudioCodec(AudioCodec.Copy)
                )
                .NotifyOnError(Console.WriteLine)
                .CancellableThrough(CancellationToken)
                .ProcessAsynchronously();
            
            // await FFMpegArguments
            //     // Input
            //     .FromFileInput(fileToConvert.FullName, true, options => options
            //             .WithHardwareAcceleration(HardwareAccelerationDevice.CUDA)
            //             // .WithCustomArgument("-hwaccel cuda")                   // Usa accelerazione NVIDIA
            //             // .WithCustomArgument("-hwaccel_output_format yuv420p")     // Mantiene i frame su GPU
            //             // .WithCustomArgument("-c:v h264_cuvid")                 // Decoder NVIDIA hardware
            //     )
            //
            //     // Output
            //     .OutputToFile(Path.Combine(_toWatchFolderPath, fileToConvert.Name), true, options => options
            //             .WithCustomArgument("-map 0:v:0")
            //             .WithCustomArgument("-map 0:a:0")
            //             .WithVideoCodec("hevc_nvenc")                          // Encoder NVIDIA HEVC
            //             .WithCustomArgument("-preset slow")                    // Preset qualità (alternativa a .WithSpeedPreset)
            //             .WithCustomArgument("-cq 18")                          // Controllo qualità costante (simile a -global_quality)
            //             .WithVideoFilters(filters => filters
            //                 .HardBurnSubtitle(burnOptions)
            //             )
            //             //.WithCustomArgument($"-vf hwdownload,format=yuv420p,subtitles=\"{combinedSubs.Replace("\\", "/")}\"")
            //             .WithAudioCodec(AudioCodec.Copy)                       // Copia l’audio originale
            //     )
            //
            //     .NotifyOnError(Console.WriteLine)
            //     .CancellableThrough(CancellationToken)
            //     .ProcessAsynchronously();

            _log.Info("Conversione e stampaggio dei sottotitoli del file {filename} completata", fileToConvert.Name);
            
            DeleteFile(fileToConvert);
            foreach (var subFile in subFilenames)
            {
                File.Delete(subFile);
            }
            File.Delete(combinedSubs);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error processing file {file}", fileToConvert.Name);
        }
    }

    private async Task ConvertVideo(FileInfo fileToConvert)
    {
        try
        {
            _log.Info("Inizio la conversione del video");
            await FFMpegArguments
                .FromFileInput(fileToConvert.FullName, true, options => options
                    .WithHardwareAcceleration(HardwareAccelerationDevice.QSV)
                    // .WithCustomArgument("-hwaccel qsv")     // Inizializza l'hardware QSV
                    .WithCustomArgument("-hwaccel_output_format nv12")
                    .WithCustomArgument("-c:v h264_qsv")
                )
                .OutputToFile(Path.Combine(_toWatchFolderPath, fileToConvert.Name), true, options => options
                    .WithCustomArgument("-map 0:v:0")
                    .WithCustomArgument("-map 0:a:0")
                    .WithVideoCodec("hevc_qsv")
                    .WithSpeedPreset(Speed.Slow)
                    .WithCustomArgument("-global_quality 18")
                    .WithAudioCodec(AudioCodec.Copy)
                )
                .NotifyOnError(Console.WriteLine)
                .CancellableThrough(CancellationToken)
                .ProcessAsynchronously();

            _log.Info("Conversione del video del file {filename} completata", fileToConvert.Name);
            DeleteFile(fileToConvert);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error processing file {file}", fileToConvert.Name);
        }
    }

    private static async Task CombineAssSubtitles(IEnumerable<string> files, string outputFile)
    {
        var allLines = files.SelectMany(File.ReadAllLines).ToArray();
        var firstFile = await File.ReadAllLinesAsync(files.First());

        var header = firstFile.TakeWhile(l => !l.TrimStart().StartsWith("Dialogue:")).ToList();

        List<string> ExtractDialogues(string[] lines) =>
            lines
                .SkipWhile(l => !l.Trim().Equals("[Events]", StringComparison.OrdinalIgnoreCase))
                .Where(l => l.TrimStart().StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                .ToList();

        var allEvents = files
            .SelectMany(f => ExtractDialogues(File.ReadAllLines(f)))
            .ToList();

        var sortedEvents = allEvents
            .Select(l =>
            {
                var fields = l.Split(new[] { ',' }, 10);
                if (fields.Length < 3) return (Line: l, Start: TimeSpan.MaxValue);
                return (Line: l,
                    Start: TimeSpan.TryParseExact(fields[1].Trim(), @"h\:mm\:ss\.ff", CultureInfo.InvariantCulture, out var t) ? t : TimeSpan.MaxValue);
            })
            .OrderBy(x => x.Start)
            .Select(x => x.Line)
            .ToList();

        var outputLines = header.Concat(sortedEvents).ToList();
        await File.WriteAllLinesAsync(outputFile, outputLines, new UTF8Encoding(true));
    }
    
    private async Task ExtractSubtitleTrack(String inputFile, string streamSpecifier, string outputPath)
    {
        await FFMpegArguments
            .FromFileInput(inputFile)
            .OutputToFile(outputPath, true, options => options
                    .WithCustomArgument($"-map {streamSpecifier}")
                    .WithCustomArgument("-c copy")
                    .WithCustomArgument("-f ass")
            )
            .NotifyOnError(Console.WriteLine)
            .CancellableThrough(CancellationToken)
            .ProcessAsynchronously();
    }

    private void DeleteFile(FileInfo fileToDelete)
    {
        File.Delete(fileToDelete.FullName);
    }
}