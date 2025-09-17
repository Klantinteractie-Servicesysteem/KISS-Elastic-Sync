using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.IdentityModel.Tokens;

namespace Kiss.Elastic.Sync.Sources
{
    public readonly record struct OverigObject(in JsonElement Id, in JsonElement Data);

    public sealed class ObjectenClient
    {
        private readonly HttpClient _httpClient;

        public ObjectenClient(Uri objectenBaseUri, string? objectenToken, string? objectenClientId, string? objectenClientSecret)
        {
            _httpClient = new HttpClient
            {
                BaseAddress = objectenBaseUri
            };

            if (!string.IsNullOrWhiteSpace(objectenClientId) && !string.IsNullOrWhiteSpace(objectenClientSecret))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetToken(objectenClientId, objectenClientSecret));
                return;
            }

            if (!string.IsNullOrWhiteSpace(objectenToken))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", objectenToken);
                return;
            }

            throw new Exception("No token or client id/secret is configured for the ObjectenClient");
        }

        public IAsyncEnumerable<OverigObject> GetObjecten(string type, CancellationToken token)
        {
            //  return GetObjectenInternal(type, 1, token);


            //  get openproducten
            return GetProductenInternal(type, 1,  token);
           


        }

        private static string GetToken(string id, string secret)
        {
            var now = DateTimeOffset.UtcNow;
            // one minute leeway to account for clock differences between machines
            var issuedAt = now.AddMinutes(-1);

            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(secret);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Issuer = id,
                IssuedAt = issuedAt.DateTime,
                NotBefore = issuedAt.DateTime,
                Claims = new Dictionary<string, object>
                {
                    { "client_id", id },
                    { "user_id", "KISS Elastic Sync"},
                    { "user_representation", "elastic-sync" }
                },
                Subject = new ClaimsIdentity(),
                Expires = now.AddHours(1).DateTime,
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
            };
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }



        // 33914cb3efa05a73d1e21a2d80f600f1160fff79

        private async IAsyncEnumerable<OverigObject> GetProductenInternal(string type, int page, [EnumeratorCancellation] CancellationToken token)
        {
           
            var httpClient  = new HttpClient { 
                BaseAddress = new Uri("https://openproduct.test.maykin.opengem.nl/")
            };

        //   httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetToken(objectenClientId, objectenClientSecret));
               
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", "33914cb3efa05a73d1e21a2d80f600f1160fff79");
           






           
            string? next = null;

            using (var message = new HttpRequestMessage(HttpMethod.Get, "producttypen/api/v1/producttypen"))
            {
                using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);

                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(token);
                    using var jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: token);

                    if (!jsonDoc.TryParseZgwPagination(out var pagination))
                    {
                        yield break;
                    }

                    next = pagination.Next;

                    foreach (var item in pagination.Records)
                    {
                        //if (!item.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Object ||
                        //    !record.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                        //    !item.TryGetProperty("uuid", out var uuid))
                        //{

                        if (!item.TryGetProperty("uuid", out var uuid))
                        {
                            continue;
                        }



                        using (var messageProdTypeDetails = new HttpRequestMessage(HttpMethod.Get, "producttypen/api/v1/producttypen/" + uuid))
                        {
                            using var responseProdTypeDetails = await httpClient.SendAsync(messageProdTypeDetails, HttpCompletionOption.ResponseHeadersRead, token);

                            if(!responseProdTypeDetails.IsSuccessStatusCode){
                                
                            }


                            //var r = await responseProdTypeDetails.Content.ReadAsStringAsync();
                            await using var streamDetails = await responseProdTypeDetails.Content.ReadAsStreamAsync(token);
                            using var jsonDocDetails = await JsonDocument.ParseAsync(streamDetails, cancellationToken: token);


                         




                            using (var messageProdTypeContent = new HttpRequestMessage(HttpMethod.Get, "producttypen/api/v1/producttypen/" + uuid + "/content"))
                            {
                                using var responseProdTypeContent = await httpClient.SendAsync(messageProdTypeContent, HttpCompletionOption.ResponseHeadersRead, token);

                                if (!responseProdTypeContent.IsSuccessStatusCode)
                                {
                                     
                                }


                                //var r = await responseProdTypeDetails.Content.ReadAsStringAsync();
                                await using var streamContent = await responseProdTypeContent.Content.ReadAsStreamAsync(token);
                                using var jsonDocContent = await JsonDocument.ParseAsync(streamContent, cancellationToken: token);



                                var rootDetails = jsonDocDetails.RootElement;

                                var t = JsonObject.Create(rootDetails);


                                // jsonDocDetails.RootElement.
                                
                             var x =   t.ToJsonString();  

                                var xx = x.Substring(0, x.Length - 1);
                                //xx = xx + " , \"ddd\": \"ffff\" }";
                                xx = xx + " , \"content\": " +  jsonDocContent.RootElement.ToString() + " }"; 
                                var y = JsonDocument.Parse(xx).RootElement;
                                yield return new(uuid, y);
                            }



                          

                            //  yield return new(uuid, rootDetails);

                        }

                    
                    }
                }

                // 400 probably means there is something wrong with the objecttype. ignore it.
                else if (response.StatusCode != System.Net.HttpStatusCode.BadRequest)
                {
                    await Helpers.LogResponse(response, token);
                    response.EnsureSuccessStatusCode();
                }
            }

            if (!string.IsNullOrWhiteSpace(next))
            {
                await foreach (var el in GetObjectenInternal(type, page + 1, token))
                {
                    yield return el;
                }
            }
        }


        private async IAsyncEnumerable<OverigObject> GetObjectenInternal(string type, int page, [EnumeratorCancellation] CancellationToken token)
        {
            var url = $"/api/v2/objects?type={HttpUtility.UrlEncode(type)}&page={page}";

            string? next = null;

            using (var message = new HttpRequestMessage(HttpMethod.Get, url))
            {
                using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);

                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(token);
                    using var jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: token);

                    if (!jsonDoc.TryParseZgwPagination(out var pagination))
                    {
                        yield break;
                    }

                    next = pagination.Next;

                    foreach (var item in pagination.Records)
                    {
                        if (!item.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Object ||
                            !record.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                            !item.TryGetProperty("uuid", out var uuid))
                        {
                            continue;
                        }

                        yield return new(uuid, data);
                    }
                }

                // 400 probably means there is something wrong with the objecttype. ignore it.
                else if (response.StatusCode != System.Net.HttpStatusCode.BadRequest)
                {
                    await Helpers.LogResponse(response, token);
                    response.EnsureSuccessStatusCode();
                }
            }

            if (!string.IsNullOrWhiteSpace(next))
            {
                await foreach (var el in GetObjectenInternal(type, page + 1, token))
                {
                    yield return el;
                }
            }
        }

        public void Dispose() => _httpClient?.Dispose();
    }
}
