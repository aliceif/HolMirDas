using System.Net;
using System.ServiceModel.Syndication;
using System.Text.Json;
using System.Xml;

using Flurl;
using Flurl.Http;

const string ApplicationName = "HolMirDas";
Guid applicationGuid = Guid.Parse("f61215eb-1c9e-4114-a32c-84a300ef890c");
string tempFolderName = $"{ApplicationName}_{applicationGuid:D}";
string tempFolderPath = Path.Join(Path.GetTempPath(), tempFolderName);
string processingLogFilePath = Path.Join(Path.GetTempPath(), tempFolderName, "processinglog.json");

const int maxLogEntries = 1000;
const int maxTries = 48;

Console.WriteLine("Starting HolMirDas");

// configs needed:
// list of urls (with source type)
// target instance
// access token
// size of buffer (some servers have no storage)
// location of buffer -> this should be a somewhat persistent temp file -> look up if this concept exists 

// to load RSS
// SyndicationFeed#Load takes XML Reader
// We get XML Reader probably from HTTPClient-ing the RSS URL?
// In there -> Items
// Source -> of SyndicationItem member collection links first item? (type should I think be alternate, maybe query that)

var targetToken = "";
var targetInstanceUrl = "";

IEnumerable<string> rssUrls =
[
];

HashSet<Uri> receivedUrls = [];

foreach (var rssUrl in rssUrls)
{
	using var rssXmlReader = XmlReader.Create(rssUrl);
	var feed = SyndicationFeed.Load(rssXmlReader);
	int index = 0;
	foreach (var item in feed.Items)
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

// bonus - can we pre-screen the incoming links against something like a local ringbuffer to prevent repeatedly ap/get-ing the same posts?
// can redis easily do this? -> add until we use "too much space?"

// we should also use a time filter since posts from a week+ ago are irrelevant

// to post to misskey
// simple post request to /ap/get with auth intact -> check how auth works, probably something with the app key token thing?^
// seems like auth is just an added property in request json named "i" and takes the access token
// requested remote post url is property "uri"
// for testing probably do separate user.

// this needs to avoid getting slapped by the rate limit, even apart from prefiltering
// simple delay time in the cyclical working through the set?

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
		var result = await targetInstanceUrl
			.AppendPathSegment("api")
			.AppendPathSegments("ap", "show")
			.PostJsonAsync(new
			{
				i = targetToken,
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
			Console.WriteLine($"Ran into rate limit at element {successCount + 1} / {receivedUrls.Count}");
			Console.WriteLine(ex.ToString());

			// count ratelimit as no try
			resultLog.Add(logEntry with { UrlState = UrlState.Todo });
			break;
		}
		else
		{
			Console.WriteLine(ex.ToString());

			if (logEntry.Tries < maxTries)
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
while (resultLog.Count > maxLogEntries)
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
