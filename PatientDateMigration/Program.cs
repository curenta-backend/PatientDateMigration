// See https://aka.ms/new-console-template for more information

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Azure.Security.KeyVault.Secrets;
using Azure.Identity;
using Microsoft.Extensions.Azure;
using MassTransit;
using System.Reflection;
using Domain.Interfaces.ExternalClients;
using Infrastructure.ExternalClients;
using Domain.Interfaces.DomainEvents;
using Infrastructure.DB;
using PatientsCoreAPI.Business.Helpers;
using PatientsCoreAPI.Infrastructure.Models;
using PatientsCoreAPI.Business.Services;

internal class Program
{
    private static async Task Main(string[] args)
    {
        Console.WriteLine("Hello, type Y to start data migration : ");
        var input = Console.ReadLine();
        if (input.ToLower() == "y")
        {
            Console.WriteLine("Starting data migration");

            var services = new ServiceCollection();

            var configuration = new ConfigurationBuilder().Build();

            var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var jwtSecret = string.Empty;
            if (env == "prod")
            {
                SecretClient keyVaultClient = new SecretClient(new Uri("https://authsecret.vault.azure.net/"), new DefaultAzureCredential());
                jwtSecret = keyVaultClient.GetSecret("JwtSecret").Value.Value;
            }
            else
            {
                jwtSecret = "3017a3642ab78d238ef5ab3ac18a629650888e966fa4a85ba533fb4fe2957754c2779451726bdbf690d2df5f81148c4a8824c83031b4f34afd365c3962cafccf";
            }

            services.AddHttpClient();

            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IKeyVaultManager, KeyVaultManager>();
            services.AddSingleton<IMissingPatientsNotificationQueue, MissingPatientsNotificationQueue>();
            services.AddScoped<IFacilityClient, FacilityClient>();


            // Register your DbContext and other dependencies
            services.AddDbContext<PatientContext>(options =>
                options.UseSqlServer("Server=tcp:curenta.database.windows.net,1433;Initial Catalog=test_CurentaPatients;Persist Security Info=False;User ID=curenta;Password=Theexodus#3;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"));

            //services.AddDbContext<ApplicationDbContext>(options =>
            //    options.UseSqlServer("Server=tcp:curenta.database.windows.net,1433;Initial Catalog=patientv2_test;Persist Security Info=False;User ID=curenta;Password=Theexodus#3;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"));

            services.AddAzureClients(azureClientFactoryBuilder =>
            {
                azureClientFactoryBuilder.AddSecretClient(new Uri("https://encrkeysvault.vault.azure.net/"));
            });


            services.AddMassTransit(x =>
            {
                var assembly = Assembly.GetExecutingAssembly();
                // this to guranatess having multiple prefixes for dev,test and prod environemnt and on the test/live servers have different subscription name on azure. otherwise we may get an issue when multiple devices want to register on messages using same subscription name
                x.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter($"{env}", false));
   
                x.UsingAzureServiceBus((context, cfg) =>
                {
                    cfg.Host("Endpoint=sb://curenta-messaging.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=GOLxRTENxF4yFRd+JKob/1waErgEroRWOgnrKCnt0gM=");

                    var nameFormatter = new PrefixEntityNameFormatter(cfg.MessageTopology.EntityNameFormatter, $"{env}-");
                    cfg.MessageTopology.SetEntityNameFormatter(nameFormatter);
                    cfg.ConfigureEndpoints(context);

                });

            });


            // Build the service provider
            var serviceProvider = services.BuildServiceProvider();

            // Resolve your DbContext
            using (var scope = serviceProvider.CreateScope())
            {
                var oldDbContext = serviceProvider.GetRequiredService<PatientContext>();
                //var newDbContext = serviceProvider.GetRequiredService<ApplicationDbContext>();
                var facilityClient = serviceProvider.GetRequiredService<IFacilityClient>();

                var patientDataMigration = new PatientDataMigration(oldDbContext, facilityClient);
                await patientDataMigration.MigrateAsync();
            }

            Console.WriteLine("Data migration completed");
        }
        else
        {
            Console.WriteLine("Data migration cancelled");
        }
    }
}