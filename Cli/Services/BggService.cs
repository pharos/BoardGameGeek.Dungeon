namespace BoardGameGeek.Dungeon.Services;

public interface IBggService
{
    IAsyncEnumerable<Thing> GetThingsAsync(IEnumerable<int> ids);
    IAsyncEnumerable<Collection> GetUserCollectionAsync(string userName);
    IAsyncEnumerable<Play> GetUserPlaysAsync(string userName, int? year = null, int? id = null);
    void LoginUser(IEnumerable<FlurlCookie> cookies);
    Task<IEnumerable<FlurlCookie>> LoginUserAsync(string userName, string password);
    Task<Play> LogUserPlayAsync(Play play);
}

public sealed class BggService : IBggService
{
    public sealed record PlayResponse
    {
        public int PlayId { get; set; }
        public int NumPlays { get; set; }
        public string? Html { get; set; }
        public string? Error { get; set; }
    }

    public BggService(ILogger<BggService> logger)
    {
        Logger = logger;
        FlurlClient = new FlurlClient("https://boardgamegeek.com")
        {
            Settings = { BeforeCall = call => { Logger.LogDebug($"{call.Request.Verb} {call.Request.Url}"); } }
        };
        RetryPolicy = Policy.Handle<FlurlHttpException>(ex => ex.Call.HttpResponseMessage.StatusCode == HttpStatusCode.TooManyRequests)
            .OrResult<IFlurlResponse>(response => response.ResponseMessage.StatusCode == HttpStatusCode.Accepted)
            .WaitAndRetryAsync(EnumerateDelay(), (response, _) =>
            {
                if (response.Exception is FlurlHttpException ex) //TODO once throttled, introduce delay for all subsequent calls
                {
                    Logger.LogWarning($"{ex.Call.HttpResponseMessage.StatusCode:D} {ex.Call.HttpResponseMessage.StatusCode}");
                }
                else
                {
                    Logger.LogWarning($"{response.Result.ResponseMessage.StatusCode:D} {response.Result.ResponseMessage.StatusCode}");
                }
            });
    }

    public async IAsyncEnumerable<Thing> GetThingsAsync(IEnumerable<int> ids)
    {
        async ValueTask<ThingItems> GetThingAsync(IList<int> id)
        {
            var request = FlurlClient.Request("xmlapi2", "thing")
                .SetQueryParams(new
                {
                    id = string.Join(",", id)
                });
            var response = await RetryPolicy.ExecuteAsync(() => request.GetAsync());
            return await response.ResponseMessage.Content.ReadAsAsync<ThingItems>(XmlFormatterCollection);
        }

        var thingCollections = ids.Distinct()
            .OrderBy(id => id)
            .Buffer(100)
            .ToAsyncEnumerable()
            .SelectAwait(GetThingAsync);

        await foreach (var thingCollection in thingCollections)
        {
            foreach (var item in thingCollection.Items)
            {
                yield return new Thing
                {
                    Id = item.Id,
                    Type = item.Type,
                    Name = item.Names.First().Value,
                    Image = item.Image,
                    Thumbnail = item.Thumbnail,
                    Links = item.Links.Select(link => new ThingLink
                    {
                        Id = link.Id,
                        Type = link.Type,
                        Value = link.Value,
                        IsExpansion = link.Type == "boardgameexpansion",
                        IsInbound = link.Inbound
                    }).ToArray()
                };
            }
        }
    }

    public async IAsyncEnumerable<Collection> GetUserCollectionAsync(string userName)
    {
        async Task<CollectionItems> GetCollectionAsync()
        {
            var request = FlurlClient.Request("xmlapi2", "collection")
                .SetQueryParams(new
                {
                    username = userName,
                    subtype = "boardgame",
                    minplays = 1
                });
            var response = await RetryPolicy.ExecuteAsync(() => request.GetAsync());
            return await response.ResponseMessage.Content.ReadAsAsync<CollectionItems>(XmlFormatterCollection);
        }

        var userCollection = await GetCollectionAsync();
        foreach (var item in userCollection.Items)
        {
            yield return new Collection
            {
                GameId = item.ObjectId,
                GameName = item.Name,
                Image = item.Image,
                Thumbnail = item.Thumbnail,
                TotalPlays = item.NumPlays,
                Comments = item.Comments
            };
        }
    }

    public async IAsyncEnumerable<Play> GetUserPlaysAsync(string userName, int? year = null, int? id = null)
    {
        async Task<UserPlays> GetPlaysAsync(int? page = null)
        {
            var request = FlurlClient.Request("xmlapi2", "plays")
                .SetQueryParams(new
                {
                    username = userName,
                    subtype = "boardgame",
                    id,
                    mindate = year != null ? $"{year:D4}-01-01" : null,
                    maxdate = year != null ? $"{year:D4}-12-31" : null,
                    page
                });
            var response = await RetryPolicy.ExecuteAsync(() => request.GetAsync());
            return await response.ResponseMessage.Content.ReadAsAsync<UserPlays>(XmlFormatterCollection);
        }

        var userPlays = await GetPlaysAsync();
        var totalPlays = userPlays.Total;
        var totalPages = (int)Math.Ceiling(totalPlays / 100d); // get total pages from first page
        for (var page = 1; page <= totalPages;)
        {
            foreach (var play in userPlays.Plays!)
            {
                var item = play.Items.Single();
                yield return new Play
                {
                    PlayId = play.Id,
                    GameId = item.ObjectId,
                    GameName = item.Name,
                    IsExpansion = item.Subtypes.Any(subtype => subtype.Value == "boardgameexpansion"),
                    Date = play.Date,
                    Quantity = play.Quantity,
                    Length = play.Length,
                    IsIncomplete = play.Incomplete,
                    IsSession = Play.IsSessionMatch(play.Comments),
                    NoWinStats = play.NoWinStats,
                    Location = play.Location,
                    Comments = play.Comments,
                    Players = play.Players?.Select(player => new Player
                    {
                        UserId = player.UserId,
                        UserName = player.UserName,
                        Name = player.Name,
                        StartPosition = player.StartPosition,
                        Color = player.Color,
                        Score = player.Score,
                        New = player.New,
                        Rating = player.Rating,
                        Win = player.Win
                    }).ToArray()
                };
            }
            if (page++ < totalPages)
            {
                userPlays = await GetPlaysAsync(page);
            }
        }
    }

    public void LoginUser(IEnumerable<FlurlCookie> cookies)
    {
        Cookies = new CookieJar();
        foreach (var cookie in cookies)
        {
            Cookies.AddOrReplace(cookie);
        }
    }

    public async Task<IEnumerable<FlurlCookie>> LoginUserAsync(string userName, string password)
    {
        var request = FlurlClient.Request("login", "api", "v1");
        var cookies = new CookieJar();
        var body = new
        {
            credentials = new
            {
                username = userName,
                password
            }
        };
        await RetryPolicy.ExecuteAsync(() => request.WithCookies(out cookies).PostJsonAsync(body));
        Cookies = cookies.Remove(cookie => !cookie.Name.StartsWith("bgg", StringComparison.OrdinalIgnoreCase));
        return Cookies;
    }

    public async Task<Play> LogUserPlayAsync(Play play)
    {
        var request = FlurlClient.Request("geekplay.php")
            .WithCookies(Cookies);
        var body = new
        {
            version = 2,
            ajax = 1,
            action = "save",
            objecttype = "thing",
            playdate = $"{play.Date:yyyy-MM-dd}",
            dateinput = $"{play.Date:yyyy-MM-dd}",
            location = play.Location,
            quantity = play.Quantity,
            objectid = play.GameId,
            length = play.Length,
            incomplete = play.IsIncomplete ? 1 : 0,
            nowinstats = play.NoWinStats ? 1 : 0,
            comments = play.Comments?.Replace(@"\n", "\n")
        };
        var response = await RetryPolicy.ExecuteAsync(() => request.PostUrlEncodedAsync(body)).ReceiveJson<PlayResponse>();
        play.PlayId = response.PlayId;
        return play;
    }

    private static IEnumerable<TimeSpan> EnumerateDelay()
    {
        return Enumerable.Range(2, 5).Select(seconds => TimeSpan.FromSeconds(Math.Pow(2, seconds)))
            .Concat(Enumerable.Repeat(TimeSpan.FromMinutes(1), int.MaxValue));
    }

    private ILogger Logger { get; }
    private IFlurlClient FlurlClient { get; }
    private CookieJar? Cookies { get; set; }
    private AsyncRetryPolicy<IFlurlResponse> RetryPolicy { get; }

    private static readonly XmlMediaTypeFormatter XmlFormatter = new() { UseXmlSerializer = true };
    private static readonly MediaTypeFormatterCollection XmlFormatterCollection = new(new MediaTypeFormatter[] { XmlFormatter });
}
