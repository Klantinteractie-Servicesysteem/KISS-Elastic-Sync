﻿using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

            var headers = _httpClient.DefaultRequestHeaders;
            headers.Add("Content-Crs", "EPSG:4326");

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
            var url = $"/api/v2/objects?type={HttpUtility.UrlEncode(type)}";
            return GetObjectenInternal(url, token);
        }

        public async Task SaveAll(IAsyncEnumerable<JsonObject> docs, string type, int typeVersion = 1)
        {
            var url = "/api/v2/objects";
            var startAt = DateTime.Now.ToString("yyyy-MM-dd");
            await foreach (var doc in docs)
            {               
                var wrapper = new JsonObject
                {
                    ["type"] = type,
                    ["record"] = new JsonObject
                    {
                        ["typeVersion"] = typeVersion,
                        ["startAt"] = startAt,
                        ["data"] = doc.DeepClone()
                    }
                };
                using var response = await _httpClient.PostAsJsonAsync(url, wrapper);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                }
                response.EnsureSuccessStatusCode();
            }
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

        private async IAsyncEnumerable<OverigObject> GetObjectenInternal(string url, [EnumeratorCancellation] CancellationToken token)
        {
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
                await foreach (var el in GetObjectenInternal(next, token))
                {
                    yield return el;
                }
            }
        }

        public void Dispose() => _httpClient?.Dispose();
    }
}
