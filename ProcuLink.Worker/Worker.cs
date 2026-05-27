using Hangfire;
using ProcuLink.Worker.Jobs;

namespace ProcuLink.Worker;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IRecurringJobManager _recurringJobs;

    public Worker(ILogger<Worker> logger, IRecurringJobManager recurringJobs)
    {
        _logger = logger;
        _recurringJobs = recurringJobs;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ProcuLink Worker starting up...");

        _recurringJobs.AddOrUpdate<EmailPollingJob>(
            "email-polling",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/5 * * * *");

        _logger.LogInformation("Registered recurring job: email-polling (every 5 minutes).");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProcuLink Worker is running. Waiting for jobs...");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Placeholder: In future, this will poll for pending jobs to process
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ProcuLink Worker is stopping.");
        return base.StopAsync(cancellationToken);
    }
}
