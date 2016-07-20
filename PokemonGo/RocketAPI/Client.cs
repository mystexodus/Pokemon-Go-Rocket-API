using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Google.Protobuf;
using PokemonGo.RocketAPI.Enums;
using PokemonGo.RocketAPI.GeneratedCode;
using PokemonGo.RocketAPI.Helpers;
using PokemonGo.RocketAPI.Extensions;
using PokemonGo.RocketAPI.Login;
using System.Linq;

namespace PokemonGo.RocketAPI
{
    public class Client
    {
        private readonly ISettings _settings;
        private readonly HttpClient _httpClient;
        private AuthType _authType = AuthType.Google;
        private string _accessToken;
        private string _apiUrl;
        private Request.Types.UnknownAuth _unknownAuth;
        private readonly Random _random = new Random();
        private const double LocationRandomizationInMeters = 1f;

        private GeoCoordinate _currentLocation;

        public Client(ISettings settings)
        {
            _settings = settings;
            SetCoordinates(_settings.DefaultLatitude, _settings.DefaultLongitude);

            //Setup HttpClient and create default headers
            HttpClientHandler handler = new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = false
            };
            _httpClient = new HttpClient(new RetryHandler(handler));
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Niantic App");
            //"Dalvik/2.1.0 (Linux; U; Android 5.1.1; SM-G900F Build/LMY48G)");
            _httpClient.DefaultRequestHeaders.ExpectContinue = false;
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Connection", "keep-alive");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type",
                "application/x-www-form-urlencoded");
        }

        private void SetCoordinates(double lat, double lng)
        {
            _currentLocation = new GeoCoordinate(lat, lng);
        }

        public async Task DoGoogleLogin()
        {
            if (_settings.GoogleRefreshToken == string.Empty)
            {
                var tokenResponse = await GoogleLogin.GetAccessToken();
                _accessToken = tokenResponse.id_token;
                _settings.GoogleRefreshToken = tokenResponse.access_token;
            }
            else
            {
                var tokenResponse = await GoogleLogin.GetAccessToken(_settings.GoogleRefreshToken);
                _accessToken = tokenResponse.id_token;
                _authType = AuthType.Google;
            }
        }

        public async Task DoPtcLogin(string username, string password)
        {
            _accessToken = await PtcLogin.GetAccessToken(username, password);
            _authType = AuthType.Ptc;
        }

        private GeoCoordinate RandomizeLocation(GeoCoordinate location)
        {
            var radiusInDegrees = LocationRandomizationInMeters / 111300f;

            var u = _random.NextDouble();
            var v = _random.NextDouble();
            var w = radiusInDegrees * Math.Sqrt(u);
            var t = 2 * Math.PI * v;
            var x = w * Math.Cos(t);
            var y = w * Math.Sin(t);

            // Adjust the x-coordinate for the shrinking of the east-west distances
            x = x / Math.Cos(location.Latitude);

            return new GeoCoordinate(y + location.Latitude, x + location.Longitude);
        }

        private async Task<PlayerUpdateResponse> DoWalk()
        {
            var randomizedLocation = RandomizeLocation(_currentLocation);

            var customRequest = new Request.Types.PlayerUpdateProto
            {
                Lat = Utils.FloatAsUlong(randomizedLocation.Latitude),
                Lng = Utils.FloatAsUlong(randomizedLocation.Longitude)
            };

            var updateRequest = RequestBuilder.GetRequest(_unknownAuth, randomizedLocation.Latitude, randomizedLocation.Longitude, 10,
                new Request.Types.Requests
                {
                    Type = (int)RequestType.PLAYER_UPDATE,
                    Message = customRequest.ToByteString()
                });
            var updateResponse =
                await _httpClient.PostProtoPayload<Request, PlayerUpdateResponse>($"https://{_apiUrl}/rpc", updateRequest);

            return updateResponse;
        }

        public async Task<PlayerUpdateResponse> UpdatePlayerLocation(double lat, double lng)
        {
            PlayerUpdateResponse updateResponse = null;

            if (_currentLocation.Latitude >= lat)
            {
                while (_currentLocation.Latitude > lat)
                {
                    SetCoordinates(_currentLocation.Latitude - 0.000095, _currentLocation.Longitude);
                    updateResponse = await DoWalk();
                    await Task.Delay(100);
                }
            }
            else
            {
                while (_currentLocation.Latitude < lat)
                {
                    SetCoordinates(_currentLocation.Latitude + 0.000095, _currentLocation.Longitude);
                    updateResponse = await DoWalk();
                    await Task.Delay(100);
                }
            }

            if (_currentLocation.Longitude >= lng)
            {
                while (_currentLocation.Longitude > lng)
                {
                    SetCoordinates(_currentLocation.Latitude, _currentLocation.Longitude - 0.000095);
                    updateResponse = await DoWalk();
                    await Task.Delay(100);
                }
            }
            else
            {
                while (_currentLocation.Longitude < lng)
                {
                    SetCoordinates(_currentLocation.Latitude, _currentLocation.Longitude + 0.000095);
                    updateResponse = await DoWalk();
                    await Task.Delay(100);
                }
            }

            return updateResponse;
        }

        public async Task SetServer()
        {
            var randomizedLocation = RandomizeLocation(_currentLocation);
            var serverRequest = RequestBuilder.GetInitialRequest(_accessToken, _authType, randomizedLocation.Latitude, randomizedLocation.Longitude, 10,
                 RequestType.GET_PLAYER, RequestType.GET_HATCHED_OBJECTS, RequestType.GET_INVENTORY,
                 RequestType.CHECK_AWARDED_BADGES, RequestType.DOWNLOAD_SETTINGS);
            var serverResponse = await _httpClient.PostProto<Request>(Resources.RpcUrl, serverRequest);
            _unknownAuth = new Request.Types.UnknownAuth()
            {
                Unknown71 = serverResponse.Auth.Unknown71,
                Timestamp = serverResponse.Auth.Timestamp,
                Unknown73 = serverResponse.Auth.Unknown73,
            };

            _apiUrl = serverResponse.ApiUrl;
        }

        public async Task<GetPlayerResponse> GetProfile()
        {
            var randomizedLocation = RandomizeLocation(_currentLocation);
            var profileRequest = RequestBuilder.GetInitialRequest(_accessToken, _authType, randomizedLocation.Latitude, randomizedLocation.Longitude, 10,
                new Request.Types.Requests() { Type = (int)RequestType.GET_PLAYER });
            return await _httpClient.PostProtoPayload<Request, GetPlayerResponse>($"https://{_apiUrl}/rpc", profileRequest);
        }

        public async Task<DownloadSettingsResponse> GetSettings()
        {
            var randomizedLocation = RandomizeLocation(_currentLocation);
            var settingsRequest = RequestBuilder.GetRequest(_unknownAuth, randomizedLocation.Latitude, randomizedLocation.Longitude, 10,
                RequestType.DOWNLOAD_SETTINGS);
            return await _httpClient.PostProtoPayload<Request, DownloadSettingsResponse>($"https://{_apiUrl}/rpc", settingsRequest);
        }

        public async Task<GetMapObjectsResponse> GetMapObjects()
        {
            var randomizedLocation = RandomizeLocation(_currentLocation);
            var customRequest = new Request.Types.MapObjectsRequest()
            {
                CellIds =
                    ByteString.CopyFrom(
                        ProtoHelper.EncodeUlongList(S2Helper.GetNearbyCellIds(randomizedLocation.Longitude,
                            randomizedLocation.Latitude))),
                Latitude = Utils.FloatAsUlong(randomizedLocation.Latitude),
                Longitude = Utils.FloatAsUlong(randomizedLocation.Longitude),
                Unknown14 = ByteString.CopyFromUtf8("\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0")
            };

            var mapRequest = RequestBuilder.GetRequest(_unknownAuth, randomizedLocation.Latitude, randomizedLocation.Longitude, 10,
                new Request.Types.Requests()
                {
                    Type = (int)RequestType.GET_MAP_OBJECTS,
                    Message = customRequest.ToByteString()
                },
                new Request.Types.Requests() { Type = (int)RequestType.GET_HATCHED_OBJECTS },
                new Request.Types.Requests()
                {
                    Type = (int)RequestType.GET_INVENTORY,
                    Message = new Request.Types.Time() { Time_ = DateTime.UtcNow.ToUnixTime() }.ToByteString()
                },
                new Request.Types.Requests() { Type = (int)RequestType.CHECK_AWARDED_BADGES },
                new Request.Types.Requests()
                {
                    Type = (int)RequestType.DOWNLOAD_SETTINGS,
                    Message =
                        new Request.Types.SettingsGuid()
                        {
                            Guid = ByteString.CopyFromUtf8("4a2e9bc330dae60e7b74fc85b98868ab4700802e")
                        }.ToByteString()
                });

            return await _httpClient.PostProtoPayload<Request, GetMapObjectsResponse>($"https://{_apiUrl}/rpc", mapRequest);
        }

        public async Task<FortDetailsResponse> GetFort(string fortId, double fortLat, double fortLng)
        {
            var customRequest = new Request.Types.FortDetailsRequest()
            {
                Id = ByteString.CopyFromUtf8(fortId),
                Latitude = Utils.FloatAsUlong(fortLat),
                Longitude = Utils.FloatAsUlong(fortLng),
            };

            var randomizedLocation = RandomizeLocation(_currentLocation);
            var fortDetailRequest = RequestBuilder.GetRequest(_unknownAuth, randomizedLocation.Latitude, randomizedLocation.Longitude, 10,
                new Request.Types.Requests()
                {
                    Type = (int)RequestType.FORT_DETAILS,
                    Message = customRequest.ToByteString()
                });
            return await _httpClient.PostProtoPayload<Request, FortDetailsResponse>($"https://{_apiUrl}/rpc", fortDetailRequest);
        }

        /*num Holoholo.Rpc.Types.FortSearchOutProto.Result {
         NO_RESULT_SET = 0;
         SUCCESS = 1;
         OUT_OF_RANGE = 2;
         IN_COOLDOWN_PERIOD = 3;
         INVENTORY_FULL = 4;
        }*/

        public async Task<FortSearchResponse> SearchFort(string fortId, double fortLat, double fortLng)
        {
            var randomizedLocation = RandomizeLocation(_currentLocation);
            var customRequest = new Request.Types.FortSearchRequest()
            {
                Id = ByteString.CopyFromUtf8(fortId),
                FortLatDegrees = Utils.FloatAsUlong(fortLat),
                FortLngDegrees = Utils.FloatAsUlong(fortLng),
                PlayerLatDegrees = Utils.FloatAsUlong(randomizedLocation.Latitude),
                PlayerLngDegrees = Utils.FloatAsUlong(randomizedLocation.Longitude)
            };

            var fortDetailRequest = RequestBuilder.GetRequest(_unknownAuth, randomizedLocation.Latitude, randomizedLocation.Longitude, 30,
                new Request.Types.Requests()
                {
                    Type = (int)RequestType.FORT_SEARCH,
                    Message = customRequest.ToByteString()
                });
            return await _httpClient.PostProtoPayload<Request, FortSearchResponse>($"https://{_apiUrl}/rpc", fortDetailRequest);
        }

        public async Task<EncounterResponse> EncounterPokemon(ulong encounterId, string spawnPointGuid)
        {
            var randomizedLocation = RandomizeLocation(_currentLocation);
            var customRequest = new Request.Types.EncounterRequest()
            {
                EncounterId = encounterId,
                SpawnpointId = spawnPointGuid,
                PlayerLatDegrees = Utils.FloatAsUlong(randomizedLocation.Latitude),
                PlayerLngDegrees = Utils.FloatAsUlong(randomizedLocation.Longitude)
            };

            var encounterResponse = RequestBuilder.GetRequest(_unknownAuth, randomizedLocation.Latitude, randomizedLocation.Longitude, 30,
                new Request.Types.Requests()
                {
                    Type = (int)RequestType.ENCOUNTER,
                    Message = customRequest.ToByteString()
                });
            return await _httpClient.PostProtoPayload<Request, EncounterResponse>($"https://{_apiUrl}/rpc", encounterResponse);
        }

        public async Task<CatchPokemonResponse> CatchPokemon(ulong encounterId, string spawnPointGuid, double pokemonLat,
            double pokemonLng, MiscEnums.Item pokeball)
        {

            var customRequest = new Request.Types.CatchPokemonRequest()
            {
                EncounterId = encounterId,
                Pokeball = (int)pokeball,
                SpawnPointGuid = spawnPointGuid,
                HitPokemon = 1,
                NormalizedReticleSize = Utils.FloatAsUlong(1.950),
                SpinModifier = Utils.FloatAsUlong(1),
                NormalizedHitPosition = Utils.FloatAsUlong(1)
            };

            var randomizedLocation = RandomizeLocation(_currentLocation);
            var catchPokemonRequest = RequestBuilder.GetRequest(_unknownAuth, randomizedLocation.Latitude, randomizedLocation.Longitude, 30,
                new Request.Types.Requests()
                {
                    Type = (int)RequestType.CATCH_POKEMON,
                    Message = customRequest.ToByteString()
                });
            return
                await
                    _httpClient.PostProtoPayload<Request, CatchPokemonResponse>($"https://{_apiUrl}/rpc", catchPokemonRequest);
        }

        public async Task<MiscEnums.Item> GetPokeBall(Client client)
        {
            var inventory = await client.GetInventory();
            var ballCollection = inventory.InventoryDelta.InventoryItems
                   .Select(i => i.InventoryItemData?.Item)
                   .Where(p => p != null)
                   .GroupBy(i => (MiscEnums.Item)i.Item_)
                   .Select(kvp => new { ItemId = kvp.Key, Amount = kvp.Sum(x => x.Count) })
                   .Where(y => y.ItemId == MiscEnums.Item.ITEM_POKE_BALL
                            || y.ItemId == MiscEnums.Item.ITEM_GREAT_BALL
                            || y.ItemId == MiscEnums.Item.ITEM_ULTRA_BALL
                            || y.ItemId == MiscEnums.Item.ITEM_MASTER_BALL);

            var pokeBallsCount = ballCollection.Where(p => p.ItemId == MiscEnums.Item.ITEM_POKE_BALL).
                DefaultIfEmpty(new { ItemId = MiscEnums.Item.ITEM_POKE_BALL, Amount = 0 }).FirstOrDefault().Amount;
            var greatBallsCount = ballCollection.Where(p => p.ItemId == MiscEnums.Item.ITEM_GREAT_BALL).
                DefaultIfEmpty(new { ItemId = MiscEnums.Item.ITEM_GREAT_BALL, Amount = 0 }).FirstOrDefault().Amount;
            var ultraBallsCount = ballCollection.Where(p => p.ItemId == MiscEnums.Item.ITEM_ULTRA_BALL).
                DefaultIfEmpty(new { ItemId = MiscEnums.Item.ITEM_ULTRA_BALL, Amount = 0 }).FirstOrDefault().Amount;
            var masterBallsCount = ballCollection.Where(p => p.ItemId == MiscEnums.Item.ITEM_MASTER_BALL).
                DefaultIfEmpty(new { ItemId = MiscEnums.Item.ITEM_MASTER_BALL, Amount = 0 }).FirstOrDefault().Amount;

            if (pokeBallsCount > 0)
                return MiscEnums.Item.ITEM_POKE_BALL;

            if (greatBallsCount > 0)
                return MiscEnums.Item.ITEM_GREAT_BALL;

            if (ultraBallsCount > 0)
                return MiscEnums.Item.ITEM_ULTRA_BALL;

            if (masterBallsCount > 0)
                return MiscEnums.Item.ITEM_MASTER_BALL;

            return MiscEnums.Item.ITEM_POKE_BALL;

        }

        public async Task<TransferPokemonOut> TransferPokemon(ulong pokemonId)
        {
            var customRequest = new TransferPokemon
            {
                PokemonId = pokemonId
            };

            var randomizedLocation = RandomizeLocation(_currentLocation);
            var releasePokemonRequest = RequestBuilder.GetRequest(_unknownAuth, randomizedLocation.Latitude, randomizedLocation.Longitude, 30,
                new Request.Types.Requests()
                {
                    Type = (int)RequestType.RELEASE_POKEMON,
                    Message = customRequest.ToByteString()
                });
            return await _httpClient.PostProtoPayload<Request, TransferPokemonOut>($"https://{_apiUrl}/rpc", releasePokemonRequest);
        }

        public async Task<EvolvePokemonOut> EvolvePokemon(ulong pokemonId)
        {
            var customRequest = new EvolvePokemon
            {
                PokemonId = pokemonId
            };

            var randomizedLocation = RandomizeLocation(_currentLocation);
            var releasePokemonRequest = RequestBuilder.GetRequest(_unknownAuth, randomizedLocation.Latitude, randomizedLocation.Longitude, 30,
                new Request.Types.Requests()
                {
                    Type = (int)RequestType.EVOLVE_POKEMON,
                    Message = customRequest.ToByteString()
                });
            return
                await
                    _httpClient.PostProtoPayload<Request, EvolvePokemonOut>($"https://{_apiUrl}/rpc", releasePokemonRequest);
        }

        public async Task<GetInventoryResponse> GetInventory()
        {
            var randomizedLocation = RandomizeLocation(_currentLocation);
            var inventoryRequest = RequestBuilder.GetRequest(_unknownAuth, randomizedLocation.Latitude, randomizedLocation.Longitude, 30, RequestType.GET_INVENTORY);
            return await _httpClient.PostProtoPayload<Request, GetInventoryResponse>($"https://{_apiUrl}/rpc", inventoryRequest);
        }
    }
}
