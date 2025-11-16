using System.ServiceModel.Syndication;
using System.Text.Json;
using System.Xml;

using Flurl;
using Flurl.Http;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;

const string ApplicationName = "HolMirDas";
Guid applicationGuid = Guid.Parse("f61215eb-1c9e-4114-a32c-84a300ef890c");
string tempFolderName = $"{ApplicationName}_{applicationGuid:D}";
string tempFolderPath = Path.Join(Path.GetTempPath(), tempFolderName);
string processingLogFilePath = Path.Join(Path.GetTempPath(), tempFolderName, "processinglog.json");

string configFilePath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ApplicationName, $"{ApplicationName}.json");

var configuration = new ConfigurationBuilder()
	.AddJsonFile(configFilePath, optional: true)
	.AddJsonFile($"{ApplicationName}.json", true)
	.AddEnvironmentVariables()
	.Build();

var logConfig = configuration.GetSection("Logging");

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSystemdConsole().AddConfiguration(logConfig));
var logger = loggerFactory.CreateLogger<Program>();

logger.LogInformation("Starting HolMirDas, version {Version}", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);

var config = configuration.GetSection("Config").Get<Config>();
if (config is null)
{
	logger.LogError("Error determining configuration. Please configure this application using HolMirDas.json in the usual path or via Environment.");
	return 1;
}

logger.LogInformation("HolMirDas configured");

// todo proper logging
// we probably want to send this to the journal on gnu/systemd/linux

if (config.TargetToken is null || config.TargetServerUrl is null)
{
	logger.LogError("Missing target server configuration.");
	Environment.Exit(1);
}

if (config.RssUrls.Length == 0)
{
	logger.LogWarning("No feeds configured.");
}

HashSet<Uri> receivedUrls = [];

foreach (var rssUrl in config.RssUrls)
{
	await using var xmlStream = await rssUrl.GetStreamAsync();
	using var rssXmlReader = XmlReader.Create(xmlStream);
	var feed = SyndicationFeed.Load(rssXmlReader);
	int index = 0;

	// rss is customarily sorted reverse-chronological, we want chronological to avoid loss of older entries
	foreach (var item in feed.Items.Reverse())
	{
		++index;

		if (item.Links.FirstOrDefault(l => l.RelationshipType == "alternate") is not null and var postLink)
		{
			receivedUrls.Add(postLink.Uri);
			logger.LogDebug("{Index}: {PublishDate} @ {Uri}", index, item.PublishDate, postLink.Uri);
		}
		else
		{
			logger.LogWarning("{Index}: {item.PublishDate} @ no link?", index, item.PublishDate);
		}
	}
}

logger.LogInformation("Incoming RSS Url count: {Count}", receivedUrls.Count);
var receivedLogEntries = receivedUrls.Select(u => new ProcessingLogEntry(u, UrlState.Todo, 0, DateTimeOffset.Now));

ICollection<ProcessingLogEntry> processingLog;
// read processing log
try
{
	await using (var processingLogReadStream = File.OpenRead(processingLogFilePath))
	{
		processingLog = (await JsonSerializer.DeserializeAsync<IEnumerable<ProcessingLogEntry>>(processingLogReadStream) ?? []).ToList();
	}
}
catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
{
	processingLog = [];

	Directory.CreateDirectory(tempFolderPath);
	await using var createStream = File.Create(processingLogFilePath);
	await JsonSerializer.SerializeAsync<IEnumerable<ProcessingLogEntry>>(createStream, processingLog);
}
catch (JsonException ex)
{
	logger.LogCritical("The processing log in {ProcessingLogFilePath} seems to be corrupted. Exiting now! {Exception}", processingLogFilePath, ex);
	return 1;
}

var workLog = processingLog.Concat(receivedLogEntries).DistinctBy(l => l.PostUrl).ToList();
logger.LogInformation("Log statistics before processing: ToDo {ToDo} (New {New}) Retry {Retry}", workLog.Count(p => p.UrlState == UrlState.Todo), workLog.Except(processingLog).Count(), workLog.Count(p => p.UrlState == UrlState.Retry));

var resultLog = new List<ProcessingLogEntry>(workLog.Count);

int successCount = 0;
foreach (var logEntry in workLog)
{
	if (logEntry.UrlState is UrlState.Done or UrlState.GiveUp)
	{
		resultLog.Add(logEntry);
		continue;
	}

	logger.LogDebug("Processing log entry {LogEntry}", logEntry);

	try
	{
		var result = await config.TargetServerUrl
			.AppendPathSegment("api")
			.AppendPathSegments("ap", "show")
			.PostJsonAsync(new
			{
				i = config.TargetToken,
				uri = logEntry.PostUrl.ToString(),
			})
			.ReceiveString();
		var jsonResult = JsonDocument.Parse(result);
		logger.LogTrace("Result of {PostUrl}: {JsonResult}", logEntry.PostUrl, jsonResult);

		resultLog.Add(logEntry with { UrlState = UrlState.Done });

		await Task.Delay(TimeSpan.FromSeconds(5));
		++successCount;
	}
	catch (FlurlHttpException ex)
	{
		if (ex.StatusCode == 429)
		{
			logger.LogWarning("Ran into rate limit at element {Index} / {WorkLogCount}: {Exception}", successCount + 1, workLog.Count, ex);

			// count ratelimit as no try
			resultLog.Add(logEntry with { UrlState = UrlState.Todo });
			break;
		}
		else
		{
			logger.LogError("Error at element {Index} / {WorkLogCount} ({PostUrl}): {Exception}", successCount + 1, workLog.Count, logEntry.PostUrl, ex);

			if (logEntry.Tries < config.MaxRetries)
			{
				resultLog.Add(logEntry with { UrlState = UrlState.Retry, Tries = logEntry.Tries + 1 });
			}
			else
			{
				resultLog.Add(logEntry with { UrlState = UrlState.GiveUp, Tries = logEntry.Tries + 1 });
			}

			continue;
		}
	}
}

logger.LogInformation("Work finished");

resultLog.AddRange(workLog.Where(w => !resultLog.Any(r => r.PostUrl == w.PostUrl)));

logger.LogInformation("Log statistics after processing: ToDo {ToDo} Retry {Retry}", resultLog.Count(p => p.UrlState == UrlState.Todo), resultLog.Count(p => p.UrlState == UrlState.Retry));

// trim result log if needed
while (resultLog.Count > config.MaxLogEntries)
{
	if (resultLog.Find(l => l.UrlState == UrlState.Done) is not null and var doneEntry)
	{
		resultLog.Remove(doneEntry);
		continue;
	}
	if (resultLog.Find(l => l.UrlState == UrlState.GiveUp) is not null and var giveUpEntry)
	{
		resultLog.Remove(giveUpEntry);
		continue;
	}
	else
	{
		resultLog.RemoveAt(0);
		continue;
	}
}

// write result log
await using (var processingLogWriteStream = File.Create(processingLogFilePath))
{
	await JsonSerializer.SerializeAsync<IEnumerable<ProcessingLogEntry>>(processingLogWriteStream, resultLog);
}

logger.LogInformation("HolMirDas finished");

return 0;


record class ProcessingLogEntry(Uri PostUrl, UrlState UrlState, int Tries, DateTimeOffset InitialCycleTimestamp);

enum UrlState
{
	Todo = 0,
	Retry = 1,
	Done = 2,
	GiveUp = 3,

}

class Config
{
	public string? TargetServerUrl { get; set; }
	public string? TargetToken { get; set; }
	public int MaxLogEntries { get; set; } = 1000;
	public int MaxRetries { get; set; } = 48;
	public string[] RssUrls { get; set; } = [];
}
