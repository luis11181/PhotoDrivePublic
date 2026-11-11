using Amazon.DynamoDBv2;
using Amazon.S3;
using Amazon.SimpleEmailV2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CSSApps.Shared.Infrastructure;

public static class AWSSetup
{
    /// <summary>
    ///     Add AWS infrastructure services to the service collection
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static IServiceCollection AddAWSInfrastructure(this IServiceCollection services,
        IConfiguration configuration)
    {
        //*add or configure the basic aws services options and add dependency injection of the services

        //defines the global aws options, this is not necesary if we are ok with the defaults we have in .aws folder
        var awsConfig = configuration.GetAWSOptions("Default");

        if (awsConfig == null)
            throw new ArgumentNullException(nameof(configuration), "AWS configuration section is missing");


        services.AddDefaultAWSOptions(awsConfig); // takes that configuration and creates the default aws options

        // Register AWS SES client
        // Uses shared AWS configuration from infrastructure.json (region, credentials, profile)
        // Falls back to default AWS credentials chain (IAM role, environment variables, AWS CLI config)
        services.AddAWSService<IAmazonSimpleEmailServiceV2>();
        services.AddAWSService<IAmazonS3>();
        
        // Register AWS clients as necessary, e.g., Amazon DynamoDB
        services.AddAWSService<IAmazonDynamoDB>();

        // the alternative is to manually register the singleton
        //services.AddSingleton<IAmazonDynamoDB>(sp =>
        //{
        //    var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.USEast1);
        //    return client;
        //});


        return services;
    }
}
