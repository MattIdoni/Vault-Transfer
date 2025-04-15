using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

class VaultTransferExample
{
    static async Task Main(string[] args)
    {
        bool interactive = IsInteractive();

        // Read configuration inputs only once at startup.
        bool overwrite = false;
        string overwriteInput = interactive
            ? Prompt("Do you want to enable overwrite? (true/false): ", "false")
            : Environment.GetEnvironmentVariable("OVERWRITE") ?? "false";
        bool.TryParse(overwriteInput, out overwrite);

        // Read Vault configuration
        string vaultAddress = interactive
            ? Prompt("Enter HashiCorp Vault Address (e.g., http://127.0.0.1:8200): ", "http://127.0.0.1:8200")
            : Environment.GetEnvironmentVariable("VAULT_ADDR") ?? "http://127.0.0.1:8200";
        if (!vaultAddress.EndsWith("/"))
        {
            vaultAddress += "/";
        }
        string vaultToken = interactive
            ? ReadSecretInput("Enter HashiCorp Vault Token: ")
            : Environment.GetEnvironmentVariable("VAULT_TOKEN");

        // Read Azure Key Vault configuration
        string azureKeyVaultUrl = interactive
            ? Prompt("Enter Azure Key Vault URL (e.g., https://your-key-vault-name.vault.azure.net/): ", "")
            : Environment.GetEnvironmentVariable("AZURE_KEY_VAULT_URL");
        if (!string.IsNullOrWhiteSpace(azureKeyVaultUrl) && !azureKeyVaultUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            azureKeyVaultUrl = "https://" + azureKeyVaultUrl;
        }
        azureKeyVaultUrl = azureKeyVaultUrl?.TrimEnd('/');

        string azureTenantId = interactive
            ? Prompt("Enter Azure Tenant ID: ", "")
            : Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        string azureClientId = interactive
            ? Prompt("Enter Azure Client ID: ", "")
            : Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        string azureClientSecret = interactive
            ? ReadSecretInput("Enter Azure Client Secret: ")
            : Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");

        // Get scheduling time once at startup.
        string scheduleTimeInput = interactive
            ? Prompt("Enter time (HH:mm) to redo the secret transfer (or leave empty to run once): ")
            : Environment.GetEnvironmentVariable("SCHEDULE_TIME");

        TimeSpan scheduledTime = TimeSpan.Zero;
        bool schedulingEnabled = !string.IsNullOrWhiteSpace(scheduleTimeInput) && TimeSpan.TryParse(scheduleTimeInput, out scheduledTime);

        // If scheduling is enabled, then run repeatedly; otherwise run once.
        if (schedulingEnabled)
        {
            Console.WriteLine($"Scheduling enabled. The transfer will run daily at {scheduledTime}.");
            while (true)
            {
                DateTime nextRun = GetNextOccurrence(scheduledTime);
                TimeSpan delay = nextRun - DateTime.Now;
                Console.WriteLine($"Next run scheduled at {nextRun} (in {delay.TotalMinutes:F1} minutes).");
                await Task.Delay(delay);
                await RunSecretTransferAsync(vaultAddress, vaultToken, azureKeyVaultUrl, azureTenantId, azureClientId, azureClientSecret, overwrite);
                // Small pause to avoid immediate looping in edge cases.
                await Task.Delay(1000);
            }
        }
        else
        {
            Console.WriteLine("No scheduling time provided. Running the transfer once.");
            await RunSecretTransferAsync(vaultAddress, vaultToken, azureKeyVaultUrl, azureTenantId, azureClientId, azureClientSecret, overwrite);
        }
    }

    // Calculates the next occurrence of the specified daily time.
    static DateTime GetNextOccurrence(TimeSpan scheduledTime)
    {
        DateTime now = DateTime.Now;
        DateTime todayScheduled = now.Date + scheduledTime;
        return (todayScheduled > now) ? todayScheduled : todayScheduled.AddDays(1);
    }

    // Contains the secret transfer logic from Azure Key Vault to HashiCorp Vault.
    static async Task RunSecretTransferAsync(string vaultAddress, string vaultToken, string azureKeyVaultUrl,
                                                string azureTenantId, string azureClientId, string azureClientSecret,
                                                bool overwrite)
    {
        // Validate Vault URI
        if (!Uri.TryCreate(vaultAddress, UriKind.Absolute, out Uri vaultUri))
        {
            Console.WriteLine("Invalid HashiCorp Vault Address provided.");
            return;
        }

        // Create HttpClient using Vault settings.
        using (var httpClient = new HttpClient { BaseAddress = vaultUri })
        {
            httpClient.DefaultRequestHeaders.Add("X-Vault-Token", vaultToken);

            // Validate and process Azure Key Vault URL.
            if (string.IsNullOrWhiteSpace(azureKeyVaultUrl))
            {
                Console.WriteLine("Azure Key Vault URL cannot be empty.");
                return;
            }

            // Create Azure credential and secret client.
            var credential = new ClientSecretCredential(azureTenantId, azureClientId, azureClientSecret);
            var secretClient = new SecretClient(new Uri(azureKeyVaultUrl), credential);

            // Retrieve secrets from Azure Key Vault.
            Console.WriteLine("Retrieving secrets from Azure Key Vault...");
            var azureSecrets = new Dictionary<string, string>();
            await foreach (var secretProperties in secretClient.GetPropertiesOfSecretsAsync())
            {
                try
                {
                    KeyVaultSecret secret = await secretClient.GetSecretAsync(secretProperties.Name);
                    azureSecrets.Add(secret.Name, secret.Value);
                    Console.WriteLine($"Retrieved secret: {secret.Name}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to retrieve secret '{secretProperties.Name}': {ex.Message}");
                }
            }
            Console.WriteLine($"Total secrets retrieved from Azure Key Vault: {azureSecrets.Count}");

            // Transfer each secret to HashiCorp Vault.
            foreach (var kvp in azureSecrets)
            {
                string secretName = kvp.Key;
                string secretValue = kvp.Value;

                // Check if the secret already exists in Vault.
                bool secretExists = false;
                string vaultMetadataEndpoint = $"v1/secret/metadata/{secretName}";
                HttpResponseMessage metadataResponse = await httpClient.GetAsync(vaultMetadataEndpoint);
                if (metadataResponse.IsSuccessStatusCode)
                {
                    secretExists = true;
                }

                if (secretExists && !overwrite)
                {
                    Console.WriteLine($"Secret '{secretName}' already exists in Vault and overwrite is disabled. Skipping.");
                    continue;
                }

                // Prepare the payload per Vault KV v2 API.
                var secretPayload = new { data = new { value = secretValue } };
                string jsonPayload = JsonSerializer.Serialize(secretPayload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                string vaultSecretEndpoint = $"v1/secret/data/{secretName}";
                HttpResponseMessage response = await httpClient.PostAsync(vaultSecretEndpoint, content);

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Secret '{secretName}' transferred successfully!");
                }
                else
                {
                    Console.WriteLine($"Error transferring secret '{secretName}': {response.StatusCode}");
                }
            }
        }
    }

    // Helper method for non-secret prompts.
    static string Prompt(string message, string defaultValue = "")
    {
        Console.Write(message);
        string input = Console.ReadLine();
        return string.IsNullOrWhiteSpace(input) ? defaultValue : input.Trim();
    }

    // Helper method to read sensitive input without echoing.
    static string ReadSecretInput(string promptMessage)
    {
        Console.Write(promptMessage);
        var secret = ReadHiddenInput();
        int currentLine = Console.CursorTop - 1;
        if (currentLine >= 0)
        {
            Console.SetCursorPosition(0, currentLine);
            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, currentLine);
        }
        return secret;
    }

    // Reads input from the console without echoing characters.
    static string ReadHiddenInput()
    {
        StringBuilder input = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            else if (key.Key == ConsoleKey.Backspace && input.Length > 0)
            {
                input.Remove(input.Length - 1, 1);
                Console.Write("\b \b");
            }
            else
            {
                input.Append(key.KeyChar);
            }
        }
        return input.ToString();
    }

    // Determines if the application is running interactively.
    static bool IsInteractive()
    {
        return Environment.UserInteractive;
    }
}