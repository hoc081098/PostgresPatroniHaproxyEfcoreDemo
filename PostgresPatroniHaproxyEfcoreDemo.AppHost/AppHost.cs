var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.PostgresPatroniHaproxyEfcoreDemo>("postgrespatronihaproxyefcoredemo");

builder.Build().Run();
