namespace ProcuLink.Worker;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ProcuLink Worker starting up...");
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
