using Amazon.SageMakerRuntime;
using Amazon.SageMakerRuntime.Model;
using Microsoft.Extensions.Configuration;

namespace MusicAI.Infrastructure.Services
{
    public class SageMakerClientService
    {
        private readonly AmazonSageMakerRuntimeClient _client;
        private readonly string _endpoint;

        public SageMakerClientService(IConfiguration cfg)
        {
            _client = new AmazonSageMakerRuntimeClient(cfg["AWS:AccessKey"], cfg["AWS:SecretKey"],
                Amazon.RegionEndpoint.GetBySystemName(cfg["AWS:Region"]));
            _endpoint = cfg["AWS:SageMakerEndpointName"];
        }

        public async Task<string> InvokeModelAsync(string jsonPayload)
        {
            var req = new InvokeEndpointRequest
            {
                EndpointName = _endpoint,
                ContentType = "application/json",
                Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(jsonPayload))
            };

            var res = await _client.InvokeEndpointAsync(req);
            using var sr = new StreamReader(res.Body);
            return await sr.ReadToEndAsync();
        }
    }
}
