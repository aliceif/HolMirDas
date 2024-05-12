# HolMirDas
A tool to fetch posts into Misskey based on Hashtags, Profiles, and other RSS sources

This tool is designed to be ran from something like a systemd timer.
On the default Misskey account configuration you may run into a rate limit at ~ 30 posts per hour. Adjust misskey config accordingly and be mindful of your cycle time and number of feeds.

Configuration is done in a file named "HolMirDas.json" in the program folder or XDG_CONFIG_HOME/HolMirDas. Configuration can also be passed in the environment according to normal .NET 8 conventions.
An example config may look like this:

```json
{
        "Logging": {"LogLevel": {"Default": "Warning"}},
        "Config": {
                "TargetServerUrl": "https://misskey.example.com",
                "TargetToken": "YOUR_TOKEN_HERE",
                "MaxLogEntries": 1000,
                "MaxRetries": 48,
                "RssUrls": [
                        "https://social.example.com/tags/hashtag.rss",
                        "https://social.example.com/@announcements.rss"
                ]
        }
 }
```
