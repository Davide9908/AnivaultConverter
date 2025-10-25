using Coravel.Invocable;
using FFMpegCore;
using FFMpegCore.Arguments;
using FFMpegCore.Enums;
using FFMpegCore.Pipes;

namespace AnivaultConverter;

public class VideoConverterTask : IInvocable, ICancellableInvocable
{
    private const string DownloadingPrefix = "downloading_";
    private const string H264Codec = "h264";
    private readonly string[] _availableExtensions = [".mp4", ".mkv"];
    private readonly string _downloadingFolderPath;
    private readonly string _toWatchFolderPath;
    private readonly ILogger<VideoConverterTask> _log;

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
            .Where(fi => _availableExtensions.Contains(fi.Extension) && fi.Name.StartsWith(DownloadingPrefix))
            .ToList();
        foreach (FileInfo fileToConvert in fileToConvertList)
        {
            try
            {
                IMediaAnalysis mediaInfo = await FFProbe.AnalyseAsync(fileToConvert.FullName, null, CancellationToken);
                SubtitleStream? subs = mediaInfo.SubtitleStreams.FirstOrDefault(s => s.Language == "ita");
                if (mediaInfo.VideoStreams.First().CodecName != H264Codec && subs == null)
                {
                    File.Move(fileToConvert.FullName, Path.Combine(_toWatchFolderPath, fileToConvert.Name));
                    continue;
                }

                if (subs is null)
                {
                    await ConvertVideo(fileToConvert);
                    DeleteFile(fileToConvert);
                    return;
                }
                await ConvertAndPrintSubs(fileToConvert, subs);
                DeleteFile(fileToConvert);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error processing file {file}", fileToConvert.Name);
            }
        }

    }

    private async Task ConvertAndPrintSubs(FileInfo fileToConvert, SubtitleStream subsInfo)
    {
        var subtitleOptions = SubtitleHardBurnOptions.Create(fileToConvert.FullName);
        subtitleOptions.SetSubtitleIndex(subsInfo.Index);
        _log.Info("Inizio la conversione e stampaggio dei sottotitoli");
        await FFMpegArguments
            // -i "filepath"
            .FromFileInput(fileToConvert.FullName)

            // "newfilepath" (e opzioni)
            .OutputToFile(Path.Combine(_toWatchFolderPath, fileToConvert.Name), true, options => options
                // -map 0:v:0 (METODO CORRETTO)
                .WithCustomArgument("-map 0:v:0")

                // -map 0:a:0 (METODO CORRETTO)
                .WithCustomArgument("-map 0:a:0")

                // -c:v hevc_qsv
                .WithVideoCodec("hevc_qsv")

                // -preset medium
                .WithSpeedPreset(Speed.Medium)

                // -global_quality 24
                .WithCustomArgument("-global_quality 24")

                // -vf "subtitles='...':si=0"
                .WithVideoFilters(filters => filters
                    // 3. Passa l'oggetto opzioni già configurato
                    .HardBurnSubtitle(subtitleOptions)
                )

                // -c:a copy
                .WithAudioCodec(AudioCodec.Copy)
            )
            .NotifyOnError(Console.WriteLine)
            .CancellableThrough(CancellationToken)
            .ProcessAsynchronously(true);
        
        _log.Info("Conversione e stampaggio dei sottotitoli del file {filename} completata", fileToConvert.Name);
    }
    
    private async Task ConvertVideo(FileInfo fileToConvert)
    {
        
        _log.Info("Inizio la conversione del video");
        await FFMpegArguments
            // -i "filepath"
            .FromFileInput(fileToConvert.FullName)

            // "newfilepath" (e opzioni)
            .OutputToFile(Path.Combine(_toWatchFolderPath, fileToConvert.Name), true, options => options
                // -map 0:v:0 (METODO CORRETTO)
                .WithCustomArgument("-map 0:v:0")

                // -map 0:a:0 (METODO CORRETTO)
                .WithCustomArgument("-map 0:a:0")

                // -c:v hevc_qsv
                .WithVideoCodec("hevc_qsv")

                // -preset medium
                .WithSpeedPreset(Speed.Medium)

                // -global_quality 24
                .WithCustomArgument("-global_quality 24")

                // -c:a copy
                .WithAudioCodec(AudioCodec.Copy)
            )
            .NotifyOnError(Console.WriteLine)
            .CancellableThrough(CancellationToken)
            .ProcessAsynchronously(true);
        
        _log.Info("Conversione del video del file {filename} completata", fileToConvert.Name);
    }

    private void DeleteFile(FileInfo fileToDelete)
    {
        File.Delete(fileToDelete.FullName);
    }
}