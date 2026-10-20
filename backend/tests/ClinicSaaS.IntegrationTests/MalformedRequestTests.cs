using System.Net;
using System.Text;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Phase 1 local audit (P1-01): a body that is not valid JSON is a client error with the unified 400 body,
/// never a 500 from the model-state error mapper.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MalformedRequestTests(ApiFactory factory)
{
    [Theory]
    [InlineData("{\"fullName\": \"x\", bad json")]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"fullName\": 123, \"gender\": \"Male\", \"identityType\": \"None\", \"dateOfBirth\": \"not-a-date\"}")]
    public async Task Malformed_json_body_is_400_validation_failed(string body)
    {
        var a = await factory.RegisterClinicAsync();
        var res = await a.Client.PostAsync("/api/v1/patients", new StringContent(body, Encoding.UTF8, "application/json"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.ErrorCodeAsync()).Should().Be("VALIDATION_FAILED");
    }
}
