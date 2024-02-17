// See https://aka.ms/new-console-template for more information
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


// bonus - can we pre-screen the incoming links against something like a local ringbuffer to prevent repeatedly ap/get-ing the same posts?

// to post to misskey
// simple post request to /ap/get with auth intact -> check how auth works, probably something with the app key token thing?^
// seems like auth is just an added property in request json named "i" and takes the access token
// requested remote post url is property "uri"
// for testing probably do separate user.

Console.WriteLine("HolMirDas finished");
