using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeebe.Client;
using Zeebe.Client.Api.Responses;
using Zeebe.Client.Api.Worker;
using Newtonsoft.Json;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddHostedService<PaymentWorkerService>();
    })
    .Build();

await host.RunAsync();

/// <summary>
/// Background service that deploys the BPMN process and registers job workers for all service tasks.
/// </summary>
public class PaymentWorkerService : BackgroundService
{
    private readonly ILogger<PaymentWorkerService> _logger;
    private static readonly Random Rng = new();

    // Zeebe gateway address — configurable via environment variable
    private readonly string _zeebeAddress;

    public PaymentWorkerService(ILogger<PaymentWorkerService> logger)
    {
        _logger = logger;
        _zeebeAddress = Environment.GetEnvironmentVariable("ZEEBE_ADDRESS") ?? "127.0.0.1:26500";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Connecting to Zeebe gateway at {Address}...", _zeebeAddress);

        var client = ZeebeClient.Builder()
            .UseGatewayAddress(_zeebeAddress)
            .UsePlainText()
            .Build();

        // Wait until Zeebe is ready
        await WaitForZeebe(client, stoppingToken);

        // Deploy the BPMN process
        await DeployProcess(client);

        // Open job workers for every service task type defined in the BPMN
        var workers = new List<IJobWorker>
        {
            CreateWorker(client, "create-order", HandleCreateOrder),
            CreateWorker(client, "hold-funds", HandleHoldFunds),
            CreateWorker(client, "fraud-check", HandleFraudCheck),
            CreateWorker(client, "request-manual-check", HandleRequestManualCheck),
            CreateWorker(client, "capture-funds", HandleCaptureFunds),
            CreateWorker(client, "notify-client", HandleNotifyClient),
            CreateWorker(client, "return-funds", HandleReturnFunds),
            CreateWorker(client, "notify-security-dep", HandleNotifySecurityDep),
            CreateWorker(client, "cancel-order", HandleCancelOrder),
        };

        _logger.LogInformation("All {Count} job workers started. Waiting for jobs...", workers.Count);

        // Keep alive until the host shuts down
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }

        // Dispose workers
        foreach (var w in workers) w.Dispose();
        client.Dispose();

        _logger.LogInformation("Payment worker service stopped.");
    }

    // ───────────── Deployment ─────────────

    private async Task DeployProcess(IZeebeClient client)
    {
        var bpmnPath = Path.Combine(AppContext.BaseDirectory, "bpmn_flow.bpmn");
        if (!File.Exists(bpmnPath))
            bpmnPath = "bpmn_flow.bpmn";

        var formPath = Path.Combine(AppContext.BaseDirectory, "form_manual_check.form");
        if (!File.Exists(formPath))
            formPath = "form_manual_check.form";

        _logger.LogInformation("Deploying BPMN process and form...");
        try
        {
            // zb-client throws NotImplementedException when parsing Form resource
            // in the response, but Zeebe actually deploys both resources successfully.
            var response = await client.NewDeployCommand()
                .AddResourceFile(bpmnPath)
                .AddResourceFile(formPath)
                .Send();
            _logger.LogInformation("Deployed. Key = {Key}", response.Key);
        }
        catch (NotImplementedException)
        {
            // Expected: zb-client 2.5.0 can't parse Form type in deploy response.
            // The deployment itself succeeded on Zeebe side.
            _logger.LogInformation("BPMN and Form deployed (zb-client Form parse workaround).");
        }
    }

    // ───────────── Wait for Zeebe ─────────────

    private async Task WaitForZeebe(IZeebeClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var topology = await client.TopologyRequest().Send();
                _logger.LogInformation("Zeebe is ready. Brokers: {Count}", topology.Brokers.Count);
                return;
            }
            catch
            {
                _logger.LogWarning("Zeebe not ready yet, retrying in 5 s...");
                await Task.Delay(5000, ct);
            }
        }
    }

    // ───────────── Worker factory ─────────────

    private IJobWorker CreateWorker(IZeebeClient client, string jobType, AsyncJobHandler handler)
    {
        return client.NewWorker()
            .JobType(jobType)
            .Handler(handler)
            .MaxJobsActive(5)
            .Name($"payment-worker-{jobType}")
            .PollInterval(TimeSpan.FromSeconds(1))
            .Timeout(TimeSpan.FromMinutes(5))
            .Open();
    }

    // ───────────── Job Handlers ─────────────

    /// <summary>1. Создать заказ</summary>
    private async Task HandleCreateOrder(IJobClient jobClient, IJob job)
    {
        var orderId = Guid.NewGuid().ToString("N")[..8];
        _logger.LogInformation("[create-order] Заказ {OrderId} создан. ProcessInstance={Inst}",
            orderId, job.ProcessInstanceKey);

        await jobClient.NewCompleteJobCommand(job.Key)
            .Variables(JsonConvert.SerializeObject(new { orderId }))
            .Send();
    }

    /// <summary>2. Удержать сумму транзакции</summary>
    private async Task HandleHoldFunds(IJobClient jobClient, IJob job)
    {
        var vars = JsonConvert.DeserializeObject<Dictionary<string, object>>(job.Variables);
        var orderId = vars?.GetValueOrDefault("orderId") ?? "?";
        _logger.LogInformation("[hold-funds] Сумма удержана для заказа {OrderId}", orderId);

        await jobClient.NewCompleteJobCommand(job.Key)
            .Variables(JsonConvert.SerializeObject(new { fundsHeld = true }))
            .Send();
    }

    /// <summary>3. Антифрод-проверка — случайный выбор из трёх сценариев</summary>
    private async Task HandleFraudCheck(IJobClient jobClient, IJob job)
    {
        // Эмуляция: 50% approve, 25% reject, 25% manual
        var roll = Rng.Next(100);
        string fraudDecision;
        if (roll < 50)
            fraudDecision = "approve";
        else if (roll < 75)
            fraudDecision = "reject";
        else
            fraudDecision = "manual";

        _logger.LogInformation("[fraud-check] Результат антифрод-проверки: {Decision}", fraudDecision);

        await jobClient.NewCompleteJobCommand(job.Key)
            .Variables(JsonConvert.SerializeObject(new { fraudDecision }))
            .Send();
    }

    /// <summary>4. Запросить ручную проверку (подготовка к User Task)</summary>
    private async Task HandleRequestManualCheck(IJobClient jobClient, IJob job)
    {
        _logger.LogInformation("[request-manual-check] Транзакция передана на ручную проверку. " +
                               "Откройте Camunda Tasklist (http://localhost:8082) для принятия решения.");

        await jobClient.NewCompleteJobCommand(job.Key)
            .Send();
    }

    /// <summary>5. Провести транзакцию (capture / перевод средств контрагенту)</summary>
    private async Task HandleCaptureFunds(IJobClient jobClient, IJob job)
    {
        var vars = JsonConvert.DeserializeObject<Dictionary<string, object>>(job.Variables);
        var orderId = vars?.GetValueOrDefault("orderId") ?? "?";
        _logger.LogInformation("[capture-funds] Средства переведены контрагенту. Заказ {OrderId}", orderId);

        await jobClient.NewCompleteJobCommand(job.Key)
            .Variables(JsonConvert.SerializeObject(new { captured = true }))
            .Send();
    }

    /// <summary>6. Уведомить клиента</summary>
    private async Task HandleNotifyClient(IJobClient jobClient, IJob job)
    {
        var vars = JsonConvert.DeserializeObject<Dictionary<string, object>>(job.Variables);
        var orderId = vars?.GetValueOrDefault("orderId") ?? "?";
        _logger.LogInformation("[notify-client] Клиент уведомлён об успешной транзакции. Заказ {OrderId}", orderId);

        await jobClient.NewCompleteJobCommand(job.Key)
            .Send();
    }

    /// <summary>7. Вернуть удержанную сумму (компенсация)</summary>
    private async Task HandleReturnFunds(IJobClient jobClient, IJob job)
    {
        var vars = JsonConvert.DeserializeObject<Dictionary<string, object>>(job.Variables);
        var orderId = vars?.GetValueOrDefault("orderId") ?? "?";
        _logger.LogInformation("[return-funds] Удержанные средства возвращены. Заказ {OrderId}", orderId);

        await jobClient.NewCompleteJobCommand(job.Key)
            .Variables(JsonConvert.SerializeObject(new { fundsReturned = true }))
            .Send();
    }

    /// <summary>8. Уведомить службу безопасности</summary>
    private async Task HandleNotifySecurityDep(IJobClient jobClient, IJob job)
    {
        var vars = JsonConvert.DeserializeObject<Dictionary<string, object>>(job.Variables);
        var orderId = vars?.GetValueOrDefault("orderId") ?? "?";
        _logger.LogInformation("[notify-security-dep] Служба безопасности уведомлена о подозрительной транзакции. Заказ {OrderId}", orderId);

        await jobClient.NewCompleteJobCommand(job.Key)
            .Send();
    }

    /// <summary>9. Отменить заказ</summary>
    private async Task HandleCancelOrder(IJobClient jobClient, IJob job)
    {
        var vars = JsonConvert.DeserializeObject<Dictionary<string, object>>(job.Variables);
        var orderId = vars?.GetValueOrDefault("orderId") ?? "?";
        _logger.LogInformation("[cancel-order] Заказ {OrderId} отменён.", orderId);

        await jobClient.NewCompleteJobCommand(job.Key)
            .Variables(JsonConvert.SerializeObject(new { orderCancelled = true }))
            .Send();
    }
}
