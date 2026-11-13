using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediathekView.Api.External;
using MediathekView.Api.External.Models;
using MediathekView.Api.Models;
using MediathekView.Api.Models.Enums;
using MediathekView.Api.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.MediathekViewDL.Tests
{
    public class MediathekViewApiClientTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task GetEntryAsync_ShouldResolveOneId_WithoutSearchPages(bool found)
        {
            // Arrange
            var handler = new Mock<HttpMessageHandler>();
            string? body = null;
            string? path = null;
            handler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .Returns(async (HttpRequestMessage request, CancellationToken _) =>
                {
                    body = await request.Content!.ReadAsStringAsync();
                    path = request.RequestUri!.AbsolutePath;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new ApiResult
                        {
                            Result = new ResultChannels
                            {
                                Results = found ? [new ResultItem { Title = "A film", Topic = "Films", Channel = "ARD", Description = "Plot", UrlVideo = "http://example.ard.de/film.mp4" }] : [],
                            },
                        })),
                    };
                });
            var client = new MediathekViewApiClient(new HttpClient(handler.Object), Mock.Of<ILogger<MediathekViewApiClient>>(),
                new TestOptionsMonitor(new MediathekViewApiClientOptions()));
            // Act
            var result = await client.GetEntryAsync("known-id", CancellationToken.None);
            // Assert
            Assert.Equal("/api/entries", path);
            Assert.Equal(new[] { "known-id" }, JsonSerializer.Deserialize<string[]>(body!));
            Assert.Equal(found, result is not null);
            if (found)
            {
                Assert.Equal("known-id", result!.Id);
                Assert.Equal("https://example.ard.de/film.mp4", Assert.Single(result.VideoUrls).Url);
            }

            handler.Protected().Verify("SendAsync", Times.Once(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
        }

        [Fact]
        public void ExactChannel_ShouldUseUpstreamKeywordField()
        {
            // Arrange
            var query = new QueryFieldsDto { Fields = [QueryFieldType.ChannelExact], Query = "SR" };
            // Act
            var model = MediathekView.Api.Converters.DtoConverter.ToModel(query);
            // Assert
            Assert.Equal("channel.keyword", Assert.Single(model.Fields));
            Assert.Equal("SR", model.Query);
        }

        [Fact]
        public async Task SearchPageAsync_ShouldKeepRawCount_WithoutFillingFilteredPagesOrProbingSizes()
        {
            // Arrange
            var handler = new Mock<HttpMessageHandler>();
            var response = new ApiResult
            {
                Result = new ResultChannels
                {
                    QueryInfo = new QueryInfo { ResultCount = 1, TotalResults = 20000, TotalRelation = "gte" },
                    Results = new Collection<ResultItem>
                    {
                        new() { Id = "one", Title = "Excluded", Topic = "Topic", Channel = "ARD", Description = "Plot", UrlVideo = "https://example.ard.de/film.mp4", Timestamp = 1700000000, Duration = 3600 },
                    },
                },
            };
            handler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) });
            var client = new MediathekViewApiClient(new HttpClient(handler.Object), Mock.Of<ILogger<MediathekViewApiClient>>(),
                new TestOptionsMonitor(new MediathekViewApiClientOptions { MaxSearchPages = 5, FetchStreamSizes = true }));
            // Act
            var result = await client.SearchPageAsync(new ApiQueryDto
            {
                Size = 1,
                Queries = [new QueryFieldsDto { Fields = [QueryFieldType.Title], Query = "Excluded", IsExclude = true }],
            }, CancellationToken.None);
            // Assert
            Assert.Empty(result.Results);
            Assert.Equal(1, result.QueryInfo.ResultCount);
            Assert.Equal(TotalRelation.GreaterThan, result.QueryInfo.TotalRelation);
            handler.Protected().Verify("SendAsync", Times.Once(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
        }

        [Fact]
        public async Task SearchAsync_ShouldRetry_WhenApiReturns500()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            var sequence = mockHttpMessageHandler
                .Protected()
                .SetupSequence<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                );

            // Fail twice with 500 Internal Server Error
            sequence.ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            sequence.ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

            // Then succeed with 200 OK and valid JSON
            var validResponse = new ApiResult
            {
                Result = new ResultChannels
                {
                    Results = new Collection<ResultItem>(),
                    QueryInfo = new QueryInfo()
                }
            };
            var json = JsonSerializer.Serialize(validResponse);
            sequence.ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions { MaxSearchPages = 5 });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act
            await client.SearchAsync(new ApiQueryDto(), CancellationToken.None);

            // Assert
            // Verify that SendAsync was called 3 times (2 failures + 1 success)
            mockHttpMessageHandler.Protected().Verify(
                "SendAsync",
                Times.Exactly(3),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            );
        }

        [Fact]
        public async Task SearchAsync_ShouldPostJsonToQueryEndpoint_WhenCalled()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            HttpRequestMessage? capturedRequest = null;
            string? capturedBody = null;

            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Returns(async (HttpRequestMessage req, CancellationToken _) =>
                {
                    capturedRequest = req;
                    if (req.Content != null)
                    {
                        capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }

                    var validResponse = new ApiResult
                    {
                        Result = new ResultChannels
                        {
                            Results = new Collection<ResultItem>(),
                            QueryInfo = new QueryInfo()
                        }
                    };
                    var json = JsonSerializer.Serialize(validResponse);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json)
                    };
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions { MaxSearchPages = 5 });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act
            await client.SearchAsync(new ApiQueryDto(), CancellationToken.None);

            // Assert
            Assert.NotNull(capturedRequest);
            Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
            Assert.Equal("https://mediathekviewweb.de/api/query", capturedRequest.RequestUri!.ToString());
            Assert.NotNull(capturedBody);
            using var parsed = JsonDocument.Parse(capturedBody!);
            Assert.True(parsed.RootElement.TryGetProperty("size", out _));
            Assert.True(parsed.RootElement.TryGetProperty("offset", out _));
        }

        [Fact]
        public async Task SearchAsync_ShouldThrowMediathekApiException_WhenApiReturns400()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.BadRequest));

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions { MaxSearchPages = 5 });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            await Assert.ThrowsAsync<MediathekApiException>(
                async () => await client.SearchAsync(new ApiQueryDto(), CancellationToken.None).ConfigureAwait(false));
        }

        [Fact]
        public async Task SearchAsync_ShouldThrowMediathekApiException_WhenApiReturns404()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions { MaxSearchPages = 5 });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            await Assert.ThrowsAsync<MediathekApiException>(
                async () => await client.SearchAsync(new ApiQueryDto(), CancellationToken.None).ConfigureAwait(false));
        }

        [Fact]
        public async Task SearchAsync_ShouldThrowMediathekApiException_WhenAllRetriesFailWith500()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions { MaxSearchPages = 5 });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            var ex = await Assert.ThrowsAsync<MediathekApiException>(
                async () => await client.SearchAsync(new ApiQueryDto(), CancellationToken.None).ConfigureAwait(false));
            Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        }

        [Fact]
        public async Task SearchAsync_ShouldThrowMediathekParsingException_WhenApiReturnsInvalidJson()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{not valid json")
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions { MaxSearchPages = 5 });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            await Assert.ThrowsAsync<MediathekParsingException>(
                async () => await client.SearchAsync(new ApiQueryDto(), CancellationToken.None).ConfigureAwait(false));
        }

        [Fact]
        public async Task SearchAsync_ShouldThrowMediathekParsingException_WhenResultIsNull()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{ \"result\": null }")
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions { MaxSearchPages = 5 });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            await Assert.ThrowsAsync<MediathekParsingException>(
                async () => await client.SearchAsync(new ApiQueryDto(), CancellationToken.None).ConfigureAwait(false));
        }

        [Fact]
        public async Task SearchAsync_ShouldSendConfiguredSize_WhenApiQueryHasSize()
        {
            // Arrange
            int? capturedSize = null;

            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Returns(async (HttpRequestMessage req, CancellationToken _) =>
                {
                    if (req.Content != null)
                    {
                        var body = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using var parsed = JsonDocument.Parse(body);
                        if (parsed.RootElement.TryGetProperty("size", out var sizeElement))
                        {
                            capturedSize = sizeElement.GetInt32();
                        }
                    }

                    var validResponse = new ApiResult
                    {
                        Result = new ResultChannels
                        {
                            Results = new Collection<ResultItem>(),
                            QueryInfo = new QueryInfo { TotalResults = 0 }
                        }
                    };
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(validResponse))
                    };
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions { MaxSearchPages = 5 });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act
            await client.SearchAsync(new ApiQueryDto { Size = 42 }, CancellationToken.None);

            // Assert
            Assert.Equal(42, capturedSize);
        }

        [Fact]
        public async Task SearchAsync_ShouldUseConfiguredApiBaseUrl_WhenSet()
        {
            // Arrange
            HttpRequestMessage? capturedRequest = null;

            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Returns(async (HttpRequestMessage req, CancellationToken _) =>
                {
                    capturedRequest = req;
                    var validResponse = new ApiResult
                    {
                        Result = new ResultChannels
                        {
                            Results = new Collection<ResultItem>(),
                            QueryInfo = new QueryInfo()
                        }
                    };
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(validResponse))
                    };
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ApiBaseUrl = "http://localhost:8000",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act
            await client.SearchAsync(new ApiQueryDto(), CancellationToken.None);

            // Assert
            Assert.NotNull(capturedRequest);
            Assert.Equal("http://localhost:8000/api/query", capturedRequest!.RequestUri!.ToString());
        }

        [Fact]
        public async Task SearchAsync_ShouldUseDefaultApiBaseUrl_WhenConfiguredValueIsEmpty()
        {
            // Arrange
            HttpRequestMessage? capturedRequest = null;

            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Returns(async (HttpRequestMessage req, CancellationToken _) =>
                {
                    capturedRequest = req;
                    var validResponse = new ApiResult
                    {
                        Result = new ResultChannels
                        {
                            Results = new Collection<ResultItem>(),
                            QueryInfo = new QueryInfo()
                        }
                    };
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(validResponse))
                    };
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ApiBaseUrl = "   ",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act
            await client.SearchAsync(new ApiQueryDto(), CancellationToken.None);

            // Assert
            Assert.NotNull(capturedRequest);
            Assert.Equal("https://mediathekviewweb.de/api/query", capturedRequest!.RequestUri!.ToString());
        }

        [Fact]
        public async Task SearchAsync_ShouldThrowMediathekApiException_WhenApiBaseUrlIsInvalid()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ApiBaseUrl = "not a valid url",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            await Assert.ThrowsAsync<MediathekApiException>(
                async () => await client.SearchAsync(new ApiQueryDto(), CancellationToken.None).ConfigureAwait(false));
        }

        [Fact]
        public async Task SearchAsync_ShouldTrimTrailingSlash_WhenApiBaseUrlEndsWithSlash()
        {
            // Arrange
            HttpRequestMessage? capturedRequest = null;

            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Returns(async (HttpRequestMessage req, CancellationToken _) =>
                {
                    capturedRequest = req;
                    var validResponse = new ApiResult
                    {
                        Result = new ResultChannels
                        {
                            Results = new Collection<ResultItem>(),
                            QueryInfo = new QueryInfo()
                        }
                    };
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(validResponse))
                    };
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ApiBaseUrl = "http://localhost:8000/",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act
            await client.SearchAsync(new ApiQueryDto(), CancellationToken.None);

            // Assert
            Assert.NotNull(capturedRequest);
            Assert.Equal("http://localhost:8000/api/query", capturedRequest!.RequestUri!.ToString());
        }

        [Fact]
        public async Task CheckConnectionAsync_ShouldCallStatsEndpoint_WhenServerRespondsCorrectly()
        {
            // Arrange
            HttpRequestMessage? capturedRequest = null;

            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Returns(async (HttpRequestMessage req, CancellationToken _) =>
                {
                    capturedRequest = req;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(MediathekViewApiClientOptions.ExpectedStatsResponse)
                    };
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ApiBaseUrl = "http://localhost:8000",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act
            await client.CheckConnectionAsync(null, CancellationToken.None);

            // Assert
            Assert.NotNull(capturedRequest);
            Assert.Equal("http://localhost:8000/stats", capturedRequest!.RequestUri!.ToString());
        }

        [Fact]
        public async Task CheckConnectionAsync_ShouldAcceptTrailingPeriod_WhenServerHttpBodyContainsOne()
        {
            // Arrange
            HttpRequestMessage? capturedRequest = null;

            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Returns(async (HttpRequestMessage req, CancellationToken _) =>
                {
                    capturedRequest = req;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(MediathekViewApiClientOptions.ExpectedStatsResponse + ".")
                    };
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ApiBaseUrl = "http://localhost:8000",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act
            await client.CheckConnectionAsync(null, CancellationToken.None);

            // Assert
            Assert.NotNull(capturedRequest);
            Assert.Equal("http://localhost:8000/stats", capturedRequest!.RequestUri!.ToString());
        }

        [Fact]
        public async Task CheckConnectionAsync_ShouldUseBaseUrlOverride_WhenProvided()
        {
            // Arrange
            HttpRequestMessage? capturedRequest = null;

            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Returns(async (HttpRequestMessage req, CancellationToken _) =>
                {
                    capturedRequest = req;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(MediathekViewApiClientOptions.ExpectedStatsResponse)
                    };
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ApiBaseUrl = "https://mediathekviewweb.de",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act
            await client.CheckConnectionAsync("http://localhost:8000", CancellationToken.None);

            // Assert
            Assert.NotNull(capturedRequest);
            Assert.Equal("http://localhost:8000/stats", capturedRequest!.RequestUri!.ToString());
        }

        [Fact]
        public async Task CheckConnectionAsync_ShouldThrowMediathekApiException_WhenResponseBodyIsUnexpected()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Hello world")
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ApiBaseUrl = "http://localhost:8000",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            var ex = await Assert.ThrowsAsync<MediathekApiException>(
                async () => await client.CheckConnectionAsync(null, CancellationToken.None).ConfigureAwait(false));
            Assert.Contains("Server is up and running", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CheckConnectionAsync_ShouldThrowMediathekApiException_WhenServerRespondsWithErrorStatusCode()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ApiBaseUrl = "http://localhost:8000",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            var ex = await Assert.ThrowsAsync<MediathekApiException>(
                async () => await client.CheckConnectionAsync(null, CancellationToken.None).ConfigureAwait(false));
            Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        }

        [Fact]
        public async Task CheckConnectionAsync_ShouldThrowMediathekConnectionException_WhenServerIsNotReachable()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ThrowsAsync(new HttpRequestException("Connection refused"));

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ApiBaseUrl = "http://localhost:8000",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            await Assert.ThrowsAsync<MediathekConnectionException>(
                async () => await client.CheckConnectionAsync(null, CancellationToken.None).ConfigureAwait(false));
        }

        [Fact]
        public async Task CheckConnectionAsync_ShouldThrowMediathekApiException_WhenBaseUrlIsInvalid()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ApiBaseUrl = "not a valid url",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            await Assert.ThrowsAsync<MediathekApiException>(
                async () => await client.CheckConnectionAsync(null, CancellationToken.None).ConfigureAwait(false));
        }

        [Fact]
        public async Task GetZappChannelsAsync_ShouldUseConfiguredZappApiBaseUrl_WhenSet()
        {
            // Arrange
            HttpRequestMessage? capturedRequest = null;

            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Returns(async (HttpRequestMessage req, CancellationToken _) =>
                {
                    capturedRequest = req;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"kika\":{\"name\":\"KiKA\",\"streamUrl\":\"http://localhost:3000/stream\"}}")
                    };
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ZappApiBaseUrl = "http://localhost:3000",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act
            await client.GetZappChannelsAsync(CancellationToken.None);

            // Assert
            Assert.NotNull(capturedRequest);
            Assert.Equal("http://localhost:3000/v1/channelInfoList", capturedRequest!.RequestUri!.ToString());
        }

        [Fact]
        public async Task GetZappChannelsAsync_ShouldTrimTrailingSlash_WhenZappApiBaseUrlEndsWithSlash()
        {
            // Arrange
            HttpRequestMessage? capturedRequest = null;

            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Returns(async (HttpRequestMessage req, CancellationToken _) =>
                {
                    capturedRequest = req;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{}")
                    };
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ZappApiBaseUrl = "http://localhost:3000/",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act
            await client.GetZappChannelsAsync(CancellationToken.None);

            // Assert
            Assert.NotNull(capturedRequest);
            Assert.Equal("http://localhost:3000/v1/channelInfoList", capturedRequest!.RequestUri!.ToString());
        }

        [Fact]
        public async Task GetZappChannelsAsync_ShouldThrowMediathekApiException_WhenZappApiBaseUrlIsInvalid()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ZappApiBaseUrl = "not a valid url",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            await Assert.ThrowsAsync<MediathekApiException>(
                async () => await client.GetZappChannelsAsync(CancellationToken.None).ConfigureAwait(false));
        }

        [Fact]
        public async Task CheckZappConnectionAsync_ShouldCallChannelInfoListEndpoint_WhenServerRespondsParsableJson()
        {
            // Arrange
            HttpRequestMessage? capturedRequest = null;

            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Returns(async (HttpRequestMessage req, CancellationToken _) =>
                {
                    capturedRequest = req;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"kika\":{\"name\":\"KiKA\",\"streamUrl\":\"http://localhost:3000/stream\"}}")
                    };
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ZappApiBaseUrl = "https://api.zapp.mediathekview.de",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act
            await client.CheckZappConnectionAsync("http://localhost:3000", CancellationToken.None);

            // Assert
            Assert.NotNull(capturedRequest);
            Assert.Equal("http://localhost:3000/v1/channelInfoList", capturedRequest!.RequestUri!.ToString());
        }

        [Fact]
        public async Task CheckZappConnectionAsync_ShouldAcceptEmptyJson_WhenBodyIsParsable()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ZappApiBaseUrl = "http://localhost:3000",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert (does not throw)
            await client.CheckZappConnectionAsync(null, CancellationToken.None);
        }

        [Fact]
        public async Task CheckZappConnectionAsync_ShouldThrowMediathekApiException_WhenResponseBodyIsNotParsable()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>not json</html>")
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ZappApiBaseUrl = "http://localhost:3000",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            var ex = await Assert.ThrowsAsync<MediathekApiException>(
                async () => await client.CheckZappConnectionAsync(null, CancellationToken.None).ConfigureAwait(false));
            Assert.Contains("non-parsable", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CheckZappConnectionAsync_ShouldThrowMediathekApiException_WhenServerRespondsWithErrorStatusCode()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ZappApiBaseUrl = "http://localhost:3000",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            var ex = await Assert.ThrowsAsync<MediathekApiException>(
                async () => await client.CheckZappConnectionAsync(null, CancellationToken.None).ConfigureAwait(false));
            Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        }

        [Fact]
        public async Task CheckZappConnectionAsync_ShouldThrowMediathekConnectionException_WhenServerIsNotReachable()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ThrowsAsync(new HttpRequestException("Connection refused"));

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ZappApiBaseUrl = "http://localhost:3000",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            await Assert.ThrowsAsync<MediathekConnectionException>(
                async () => await client.CheckZappConnectionAsync(null, CancellationToken.None).ConfigureAwait(false));
        }

        [Fact]
        public async Task CheckZappConnectionAsync_ShouldThrowMediathekApiException_WhenZappApiBaseUrlIsInvalid()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ZappApiBaseUrl = "not a valid url",
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            await Assert.ThrowsAsync<MediathekApiException>(
                async () => await client.CheckZappConnectionAsync(null, CancellationToken.None).ConfigureAwait(false));
        }

        [Fact]
        public async Task CheckConnectionAsync_ShouldThrowMediathekConnectionException_WhenServerDoesNotRespondWithinTimeout()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Returns((HttpRequestMessage _, CancellationToken ct) =>
                {
                    var tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                    ct.Register(() => tcs.TrySetCanceled());
                    return tcs.Task;
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ApiBaseUrl = "http://192.168.1.1",
                ConnectionCheckTimeoutSeconds = 1,
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            var ex = await Assert.ThrowsAsync<MediathekConnectionException>(
                async () => await client.CheckConnectionAsync(null, CancellationToken.None).ConfigureAwait(false));
            Assert.Contains("did not respond within", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CheckZappConnectionAsync_ShouldThrowMediathekConnectionException_WhenServerDoesNotRespondWithinTimeout()
        {
            // Arrange
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Returns((HttpRequestMessage _, CancellationToken ct) =>
                {
                    var tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                    ct.Register(() => tcs.TrySetCanceled());
                    return tcs.Task;
                });

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);
            var mockLogger = new Mock<ILogger<MediathekViewApiClient>>();
            var optionsMonitor = new TestOptionsMonitor(new MediathekViewApiClientOptions
            {
                ZappApiBaseUrl = "http://192.168.1.1",
                ConnectionCheckTimeoutSeconds = 1,
                MaxSearchPages = 5
            });

            var client = new MediathekViewApiClient(httpClient, mockLogger.Object, optionsMonitor);

            // Act + Assert
            var ex = await Assert.ThrowsAsync<MediathekConnectionException>(
                async () => await client.CheckZappConnectionAsync(null, CancellationToken.None).ConfigureAwait(false));
            Assert.Contains("did not respond within", ex.Message, StringComparison.Ordinal);
        }

        private sealed class TestOptionsMonitor : IOptionsMonitor<MediathekViewApiClientOptions>
        {
            private readonly MediathekViewApiClientOptions _options;

            public TestOptionsMonitor(MediathekViewApiClientOptions options)
            {
                _options = options;
            }

            public MediathekViewApiClientOptions CurrentValue => _options;

            public MediathekViewApiClientOptions Get(string? name) => _options;

            public IDisposable? OnChange(Action<MediathekViewApiClientOptions, string?> listener) => null;
        }
    }
}
