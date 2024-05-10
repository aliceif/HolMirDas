using System.Net;
using System.ServiceModel.Syndication;
using System.Text.Json;
using System.Xml;

using Flurl;
using Flurl.Http;

using Microsoft.Extensions.Configuration;

const string ApplicationName = "HolMirDas";
Guid applicationGuid = Guid.Parse("f61215eb-1c9e-4114-a32c-84a300ef890c");
string tempFolderName = $"{ApplicationName}_{applicationGuid:D}";
string tempFolderPath = Path.Join(Path.GetTempPath(), tempFolderName);
string processingLogFilePath = Path.Join(Path.GetTempPath(), tempFolderName, "processinglog.json");

string configFilePath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ApplicationName, $"{ApplicationName}.json");

Console.WriteLine("Starting HolMirDas");

var configuration = new ConfigurationBuilder()
	.AddJsonFile(configFilePath, optional: true)
	.AddJsonFile($"{ApplicationName}.json", true)
	.AddEnvironmentVariables()
	.Build();

var config = configuration.Get<Config>();
if (config is null)
{
	Console.WriteLine("Error determining configuration. Please configure this application using HolMirDas.json in the usual path or via Environment.");
	Environment.Exit(1);
}

Console.WriteLine("HolMirDas configured");

// todo proper logging
// we probably want to send this to the journal on gnu/systemd/linux

if (config.TargetToken is null || config.TargetServerUrl is null)
{
	Console.WriteLine("Missing target server configuration.");
	Environment.Exit(1);
}

if (config.RssUrls.Length == 0)
{
	Console.WriteLine("Warning: No feeds configured.");
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
			Console.WriteLine($"{index}: {item.PublishDate} @ {postLink.Uri}");
		}
		else
		{
			Console.WriteLine($"{index}: {item.PublishDate} @ no link?");
		}
	}
}

Console.WriteLine($"Incoming RSS Url count: {receivedUrls.Count}");
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

var workLog = processingLog.Concat(receivedLogEntries).DistinctBy(l => l.PostUrl).ToList();
Console.WriteLine($"Log statistics before processing: ToDo {workLog.Count(p => p.UrlState == UrlState.Todo)} (New {workLog.Except(processingLog).Count()}) Retry {workLog.Count(p => p.UrlState == UrlState.Retry)}");

var resultLog = new List<ProcessingLogEntry>(workLog.Count);

int successCount = 0;
foreach (var logEntry in workLog)
{
	if (logEntry.UrlState is UrlState.Done or UrlState.GiveUp)
	{
		resultLog.Add(logEntry);
		continue;
	}

	Console.WriteLine($"Processing log entry {logEntry}");

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
		// Console.WriteLine($"Result of {logEntry.PostUrl}:{Environment.NewLine}{jsonResult}");

		resultLog.Add(logEntry with { UrlState = UrlState.Done });

		await Task.Delay(TimeSpan.FromSeconds(5));
		++successCount;
	}
	catch (FlurlHttpException ex)
	{
		if (ex.StatusCode == 429)
		{
			Console.WriteLine($"Ran into rate limit at element {successCount + 1} / {workLog.Count}");
			Console.WriteLine(ex.ToString());

			// count ratelimit as no try
			resultLog.Add(logEntry with { UrlState = UrlState.Todo });
			break;
		}
		else
		{
			Console.WriteLine(ex.ToString());

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

Console.WriteLine("Work finished");

resultLog.AddRange(workLog.Where(w => !resultLog.Any(r => r.PostUrl == w.PostUrl)));

Console.WriteLine($"Log statistics after processing: ToDo {resultLog.Count(p => p.UrlState == UrlState.Todo)} Retry {resultLog.Count(p => p.UrlState == UrlState.Retry)}");

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
await using (var processingLogWriteStream = File.OpenWrite(processingLogFilePath))
{
	await JsonSerializer.SerializeAsync<IEnumerable<ProcessingLogEntry>>(processingLogWriteStream, resultLog);
}

Console.WriteLine("HolMirDas finished");


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
