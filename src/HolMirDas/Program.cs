using System.Net;
using System.ServiceModel.Syndication;
using System.Xml;

using Flurl;
using Flurl.Http;

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

HashSet<Uri> postUrls = [];

foreach (var rssUrl in rssUrls)
{
	using var rssXmlReader = XmlReader.Create(rssUrl);
	var feed = SyndicationFeed.Load(rssXmlReader);
	int index = 0;
	foreach (var item in feed.Items)
	{
		++index;
		Console.WriteLine($"{index}: {item.PublishDate} @ ");

		if (item.Links.FirstOrDefault(l => l.RelationshipType == "alternate") is not null and var postLink)
		{
			postUrls.Add(postLink.Uri);
			Console.WriteLine($"{index}: {item.PublishDate} @ {postLink.Uri}");
		}
		else
		{
			Console.WriteLine($"{index}: {item.PublishDate} @ no link?");
		}
	}
}

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

Console.WriteLine($"Total queue size: {postUrls.Count}");

int successCount = 0;
foreach (var postUrl in postUrls)
{
	try
	{
		var result = await targetInstanceUrl
			.AppendPathSegment("api")
			.AppendPathSegments("ap", "show")
			.PostJsonAsync(new
			{
				i = targetToken,
				uri = postUrl.ToString(),
			})
			.ReceiveString();
		var jsonResult = System.Text.Json.JsonDocument.Parse(result);
		Console.WriteLine($"Result of {postUrl}:{Environment.NewLine}{jsonResult}");

		await Task.Delay(TimeSpan.FromSeconds(5));
		++successCount;
	}
	catch (FlurlHttpException ex)
	{
		if (ex.StatusCode == 429)
		{
			Console.WriteLine($"Ran into rate limit at element {successCount} / {postUrls.Count}");
			Console.WriteLine(ex.ToString());
			break;
		}
		else
		{
			Console.WriteLine(ex.ToString());
			continue;
		}
	}
}

Console.WriteLine("HolMirDas finished");
