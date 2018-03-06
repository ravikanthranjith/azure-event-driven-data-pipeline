﻿using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.WebJobs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace ConsumerEgressFuncs
{
    public class CosmosDbIdentity
    {
        public string Id { get; set; }
        public string PartitionKey { get; set; }
    }

    public class ConsumerData
    {
        public string ConsumerUrl { get; set; }
        public List<CosmosDbIdentity> ChangedProducts { get; set; }
    }

    public static class ConsumerEgressFuncs
    {
        private static DocumentClient _documentClient;

        [FunctionName(nameof(OrchestrateConsumersFunc))]
        public static async Task OrchestrateConsumersFunc([OrchestrationTrigger] DurableOrchestrationContext ctx)
        {
            var changedProductIds = ctx.GetInput<List<CosmosDbIdentity>>();

            var retryOptions = new Microsoft.Azure.WebJobs.RetryOptions(firstRetryInterval: TimeSpan.FromSeconds(5),
                maxNumberOfAttempts: 3);

            var consumers = Environment.GetEnvironmentVariable("CONSUMERS", EnvironmentVariableTarget.Process)
                .Split(new[] { '|' });

            var parallelTasks = consumers.Select(x => CallSendToConsumerActivityAsync(ctx, retryOptions,
                new ConsumerData { ConsumerUrl = x, ChangedProducts = changedProductIds }));

            await Task.WhenAll(parallelTasks);
        }

        public static async Task CallSendToConsumerActivityAsync(DurableOrchestrationContext ctx, 
            Microsoft.Azure.WebJobs.RetryOptions retryOptions, ConsumerData consumerData)
        {
            try
            {
                await ctx.CallActivityWithRetryAsync(nameof(SendToConsumerFunc), retryOptions, consumerData);
            }
            catch
            {
                //TODO: TEMPORARILY MARK THE CONSUMER AS BANNED IN CONSUMERDB
            }
        }

        [FunctionName(nameof(SendToConsumerFunc))]
        public static async Task SendToConsumerFunc([ActivityTrigger] DurableActivityContext ctx)
        {
            var consumerData = ctx.GetInput<ConsumerData>();

            if (_documentClient == null)
                _documentClient = CreateDocumentClient();

            using (var httpClient = new HttpClient())
            {
                foreach (var product in consumerData.ChangedProducts)
                {
                    var documentUri = UriFactory.CreateDocumentUri("masterdata", "product", product.Id);
                    var document = await _documentClient.ReadDocumentAsync(documentUri, 
                        new RequestOptions { PartitionKey = new PartitionKey(product.PartitionKey) });

                    var content = new StringContent(document.ToString(), Encoding.UTF8, "application/json");
                    await httpClient.PostAsync(consumerData.ConsumerUrl, content);
                }
            }
        }

        private static DocumentClient CreateDocumentClient()
        {
            var endpoint = Environment.GetEnvironmentVariable("COSMOSDB_ENDPOINT", EnvironmentVariableTarget.Process);
            var authKey = Environment.GetEnvironmentVariable("COSMOSDB_KEY", EnvironmentVariableTarget.Process);

            return new DocumentClient(new Uri(endpoint), authKey, null, ConsistencyLevel.ConsistentPrefix);
        }
    }
}