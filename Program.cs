// This program demonstrates using Azure OpenAI Assistants API to create a weather assistant with function calling capabilities
using Azure; // Core Azure SDK classes including AzureKeyCredential for authentication
using Azure.AI.OpenAI.Assistants; // Azure OpenAI Assistants client and related models
using Microsoft.AspNetCore.Builder; // Required for WebApplication configuration
using Microsoft.Extensions.Configuration; // Provides access to app configuration and secrets
using System.Text.Json; // Used for JSON serialization/deserialization when handling function arguments

//******************************************
// Azure Reference Code
// https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/openai/Azure.AI.OpenAI.Assistants/tests/Samples/Samples_AssistantsClient.cs
//******************************************

// Initialize a web application builder to access configuration
var builder = WebApplication.CreateBuilder(args);

// Add user secrets to the configuration
// This loads sensitive information like API keys from the secrets.json file
builder.Configuration.AddUserSecrets<Program>();

// Extract configuration values from user secrets
// These values are needed to authenticate and connect to Azure OpenAI service
var azureResourceUrl = builder.Configuration["azureresourceurl"]; // The endpoint URL for your Azure OpenAI resource
var azureApiKey = builder.Configuration["azureapikey"]; // The API key for authentication
var azureApiModelName = builder.Configuration["azureaimodelname"]; // The deployed model name to use (e.g., "gpt-4")

// Create an AssistantsClient instance to interact with the Azure OpenAI Assistants API
// This client handles all API communication with the Azure OpenAI service
var client = new AssistantsClient(new Uri(azureResourceUrl),
                                   new AzureKeyCredential(azureApiKey));

#region Snippet: Define Functions and FunctionTools
// FUNCTION TOOLS DEFINITION SECTION
// This section defines three example functions that the model can instruct your app to call

// The following two lines of code (function and metadata definition) form a 'bridge' between the assistant and your code.
//   The assisant or model does not directly call the function code
//   You application receives a request from the model to invoke a function
//   The function code is executed by your application
//   The result is sent back to the assistant for further processing

// Simple function with no parameters - returns a hardcoded favorite city
string GetUserFavoriteCity() => "Seattle, WA";

// Create a metadata definition (function tool definition) that describes this function to the assistant api
// Defines the function name, description (or purpose), and parameters (none in this case)
var getUserFavoriteCityTool = new FunctionToolDefinition("getUserFavoriteCity", "Gets the user's favorite city.");


// Function with a single required parameter - returns a city's nickname
string GetCityNickname(string location) => location switch
{
    "Seattle, WA" => "The Emerald City", // Return nickname for Seattle
    _ => throw new NotImplementedException(), // Not implemented for other cities
};

// Create a function tool definition with a required 'location' parameter
var getCityNicknameTool = new FunctionToolDefinition(
    name: "getCityNickname",
    description: "Gets the nickname of a city, e.g. 'LA' for 'Los Angeles, CA'.",
    parameters: BinaryData.FromObjectAsJson( // Convert parameter schema to binary JSON data
        new
        {
            Type = "object", // Parameters will be in a JSON object
            Properties = new
            {
                Location = new // Define the location parameter
                {
                    Type = "string",
                    Description = "The city and state, e.g. San Francisco, CA",
                },
            },
            Required = new[] { "location" }, // Mark location as required
        },
        // Serialize function parameter definitions into the JSON format that the AI model expects
        // Instructs the serializer to use camelCase for JSON property names when converting 
        new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })); // Use camelCase for JSON

// Function with required and optional parameters - returns weather data
string GetWeatherAtLocation(string location, string temperatureUnit = "f") => location switch
{
    "Seattle, WA" => temperatureUnit == "f" ? "70f" : "21c", // Return weather in the requested format
    _ => throw new NotImplementedException() // Not implemented for other cities
};

// Create a function tool definition with complex parameter schema
var getCurrentWeatherAtLocationTool = new FunctionToolDefinition(
    name: "getCurrentWeatherAtLocation",
    description: "Gets the current weather at a provided location.",
    parameters: BinaryData.FromObjectAsJson(
        new
        {
            Type = "object",
            Properties = new
            {
                Location = new // Required parameter
                {
                    Type = "string",
                    Description = "The city and state, e.g. San Francisco, CA",
                },
                Unit = new // Optional parameter with enum constraints
                {
                    Type = "string",
                    Enum = new[] { "c", "f" }, // Only allow celsius or fahrenheit
                },
            },
            Required = new[] { "location" }, // Only location is required
        },
        new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
#endregion

#region Snippet:FunctionsHandleFunctionCalls

// FUNCTION CALL HANDLER
// This method processes function calls from the assistant and executes the appropriate functions
ToolOutput GetResolvedToolOutput(RequiredToolCall toolCall)
{
    // Check if this is a function tool call (as opposed to other tool types)
    if (toolCall is RequiredFunctionToolCall functionToolCall)
    {
        // Handle the getUserFavoriteCity function (has no parameters)
        if (functionToolCall.Name == getUserFavoriteCityTool.Name)
        {
            return new ToolOutput(toolCall, GetUserFavoriteCity()); // Execute function and return result
        }

        // Parse the JSON arguments provided by the assistant
        using var argumentsJson = JsonDocument.Parse(functionToolCall.Arguments);

        // Handle the getCityNickname function
        if (functionToolCall.Name == getCityNicknameTool.Name)
        {
            // Extract the location parameter from the JSON arguments
            var locationArgument = argumentsJson.RootElement.GetProperty("location").GetString();
            // Call the function with the extracted parameter
            return new ToolOutput(toolCall, GetCityNickname(locationArgument));
        }

        // Handle the getCurrentWeatherAtLocation function
        if (functionToolCall.Name == getCurrentWeatherAtLocationTool.Name)
        {
            // Extract the required location parameter
            var locationArgument = argumentsJson.RootElement.GetProperty("location").GetString();

            // Check if the optional unit parameter was provided
            if (argumentsJson.RootElement.TryGetProperty("unit", out JsonElement unitElement))
            {
                // If unit was provided, extract it and pass both parameters
                var unitArgument = unitElement.GetString();
                return new ToolOutput(toolCall, GetWeatherAtLocation(locationArgument, unitArgument));
            }
            // If unit wasn't provided, call with just location (default unit will be used)
            return new ToolOutput(toolCall, GetWeatherAtLocation(locationArgument));
        }
    }
    // If the tool call wasn't recognized, return null
    return null;
}
#endregion

#region Snippet:FunctionsCreateAssistantWithFunctionTools
// ASSISTANT CREATION
// Create an Azure OpenAI Assistant with the function tools defined above

var assistantResponse = await client.CreateAssistantAsync(
    // note: parallel function calling is only supported with newer models like gpt-4-1106-preview
    new AssistantCreationOptions(azureApiModelName) // Specify which model to use
    {
        Name = "SDK Test Assistant - Functions", // Name for the assistant
        Instructions = "You are a weather bot. Use the provided functions to help answer questions. "
            + "Customize your responses to the user's preferences as much as possible and use friendly "
            + "nicknames for cities whenever possible.", // Detailed instructions for assistant behavior
        Tools = // Register all function tools that the assistant can use
        {
                    getUserFavoriteCityTool,
                    getCityNicknameTool,
                    getCurrentWeatherAtLocationTool,
        },
    });
// Extract the assistant object from the API response
var assistant = assistantResponse.Value;
#endregion

// THREAD CREATION
// Create a new conversation thread for interacting with the assistant
var threadResponse = await client.CreateThreadAsync();
var thread = threadResponse.Value; // Store the thread for later use

// MESSAGE CREATION
// Add a user message to the thread to start the conversation
var messageResponse = await client.CreateMessageAsync(
    thread.Id, // The thread to add this message to
    MessageRole.User, // Indicate this is a user message (not assistant)
    "What's the weather like in my favorite city?"); // The actual message content
var message = messageResponse.Value;

// RUN CREATION
// Start a "run" to process the thread and generate assistant responses
var runResponse = await client.CreateRunAsync(thread, assistant);

#region Snippet:FunctionsHandlePollingWithRequiredAction
// POLLING AND FUNCTION EXECUTION LOOP
// This loop checks run status and handles any function calls needed by the assistant

do
{
    // Wait briefly to avoid excessive API calls
    await Task.Delay(TimeSpan.FromMilliseconds(500));

    // Runtime Flow:
    //  Your code polls the run status with client.GetRunAsync()
    //  When the AI model needs the app to call function(s), the API returns RunStatus.RequiresAction
    //  The RequiredAction property contains a SubmitToolOutputsAction object
    //  This object includes a collection of ToolCalls specifying which functions the AI wants to call
    runResponse = await client.GetRunAsync(thread.Id, runResponse.Value.Id);

    // Check to see if the the Assistant requires additional actions, such as a function to be executed
    if (runResponse.Value.Status == RunStatus.RequiresAction // Agent needs additional actions 
        // Check if the required action is a function call with SubmitToolOutputsAction
        // If so, the app creates a new variable inline of submitToolOutputsAction
        && runResponse.Value.RequiredAction is SubmitToolOutputsAction submitToolOutputsAction)
    {
        // Create a list to capture multiple ToolOutput objects from function calls
        // ToolOutput represents the result of a function call
        var toolOutputs = new List<ToolOutput>();

        // Iterates through requested function calls
        // ToolCalls are a collection of functions the assistant wants to call
        foreach (var toolCall in submitToolOutputsAction.ToolCalls) 
        {
            // Execute each function and collect the result.
            // It matches the function name to one of the predefined functions in the assistant creation
            // Extracts parameters provided by the AI Model
            // Calls the function with the parameters and captures the result in a ToolOutput object
            toolOutputs.Add(GetResolvedToolOutput(toolCall));
        }

        // Sends function results back to the assistant
        runResponse = await client.SubmitToolOutputsToRunAsync(runResponse.Value, toolOutputs);
    }
}
// Continue polling until the run is complete
while (runResponse.Value.Status == RunStatus.Queued
    || runResponse.Value.Status == RunStatus.InProgress);
#endregion

// MESSAGE RETRIEVAL
// Get all messages in the thread after the run is complete
var afterRunMessagesResponse = await client.GetMessagesAsync(thread.Id);
var messages = afterRunMessagesResponse.Value.Data;

// MESSAGE DISPLAY
// Display all messages in the conversation
// Note: messages iterate from newest to oldest, with the messages[0] being the most recent
foreach (var threadMessage in messages)
{
    // Format and display message metadata (timestamp and role)
    Console.Write($"{threadMessage.CreatedAt:yyyy-MM-dd HH:mm:ss} - {threadMessage.Role,10}: ");

    // Process and display each content item in the message
    foreach (var contentItem in threadMessage.ContentItems)
    {
        if (contentItem is MessageTextContent textItem)
        {
            // Display text content directly
            Console.Write(textItem.Text);
        }
        else if (contentItem is MessageImageFileContent imageFileItem)
        {
            // Display placeholder for image content
            Console.Write($"<image from ID: {imageFileItem.FileId}");
        }
        Console.WriteLine();
    }
}

// APPLICATION TERMINATION
// Wait for user input before closing the console application
Console.WriteLine("Press any key to end the session...");
Console.ReadKey();