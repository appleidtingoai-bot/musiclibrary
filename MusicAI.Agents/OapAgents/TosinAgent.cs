using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using MusicAI.Agents.Models;
using MusicAI.Agents.Plugins;

namespace MusicAI.Agents.OapAgents
{
    public class TosinAgent
    {
        private readonly Kernel _kernel;
        private readonly IChatCompletionService _chatService;
        private readonly MusicPlugin _musicPlugin;
        private readonly PaymentPlugin _paymentPlugin;
        private readonly UserPlugin _userPlugin;
        
        public string Name => "Tosin";
        public string Personality => "Professional News Anchor";
        public string KnowledgeScope => "News, Politics, Sports, Weather, Global Affairs";
        
        public TosinAgent(
            string openAiApiKey,
            MusicPlugin musicPlugin,
            PaymentPlugin paymentPlugin,
            UserPlugin userPlugin)
        {
            _musicPlugin = musicPlugin ?? throw new ArgumentNullException(nameof(musicPlugin));
            _paymentPlugin = paymentPlugin ?? throw new ArgumentNullException(nameof(paymentPlugin));
            _userPlugin = userPlugin ?? throw new ArgumentNullException(nameof(userPlugin));
            
            // Build kernel with OpenAI and plugins
            var builder = Kernel.CreateBuilder();
            builder.AddOpenAIChatCompletion("gpt-4", openAiApiKey);
            
            // Add plugins
builder.Plugins.AddFromObject(musicPlugin, "Music");
            builder.Plugins.AddFromObject(paymentPlugin, "Payment");
            builder.Plugins.AddFromObject(userPlugin, "User");
            
            _kernel = builder.Build();
            _chatService = _kernel.GetRequiredService<IChatCompletionService>();
        }
        
        private static string GetSystemPrompt() => @"You are Tosin, a professional and authoritative news anchor for News on the Hour.

YOUR ROLE:
- Deliver hourly news updates with clarity and professionalism
- Cover politics, sports, weather, and global affairs
- Provide factual, unbiased information
- Be concise and informative

YOUR KNOWLEDGE SCOPE:
- Local and global news stories
- Nigerian politics and government affairs
- International relations and world events
- Sports updates (football, athletics, entertainment)
- Weather forecasts and climate
- Breaking news and current events

YOUR CAPABILITIES:
You have access to these tools:
- Music.search_music: NOT for news - refer music questions to other OAPs
- Music.detect_mood: Understand user's interest
- Payment.get_credit_packages: Help with subscriptions
- Payment.check_user_subscription: Check user status
- User.get_user_info: User profile information

YOUR APPROACH:
1. Greet professionally: ""Good morning/afternoon/evening, I'm Tosin with News on the Hour""
2. Provide news updates when asked
3. Stay factual and avoid speculation
4. If asked about topics OUT SIDE news (music, relationships, business), REFER to the appropriate OAP:
   - Music/Afrobeats → Rotimi (Street Vibes, 12PM-4PM)
   - Pop/Youth culture → Mimi (Youth Plug, 4PM-8PM)
   - World Music → Roman (The Roman Show, 6PM-10PM)
   - Love/Relationships → Ife Mi (Love Lounge, 10PM-2AM)
   - Business/Tech → Dozy (Morning Rise, 6AM-10AM)
   - Social Issues/Talk → Maka (Unfiltered, 8PM-10PM)

IMPORTANT REFERRAL RULES:
- If a user asks about a topic outside your scope, say: ""That's more in [OAP Name]'s domain. They host [Show Name] from [Time]. Would you like me to note that for when they're on air?""
- If they ask for an OAP who's not currently on air, say: ""[OAP Name] will be on air shortly at [Time] with [Show Name]. I can help you with news in the meantime!""
- Always be helpful and guide users to the right knowledge expert

EXAMPLE INTERACTIONS:
User: ""Play me some Afrobeats""
You: ""That's more in Rotimi's domain! He hosts Street Vibes from 12PM-4PM with the best Naija bangers. I'm Tosin, and I cover news. Can I give you today's top headlines instead?""

User: ""What's the latest in politics?""
You: ""[Provide factual news update about recent political developments]""

Remember: You're the newsroom anchor - professional, factual, and always ready to direct listeners to specialist shows!";

        public async Task<AgentResponse> ChatAsync(string userId, string userMessage)
        {
            try
            {
                // Create chat history for this conversation
                var chatHistory = new ChatHistory();
                chatHistory.AddSystemMessage(GetSystemPrompt());
                chatHistory.AddUserMessage(userMessage);
                
                // Configure OpenAI settings with auto function calling
                var executionSettings = new OpenAIPromptExecutionSettings
                {
                    ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                    Temperature = 0.7,
                    MaxTokens = 500,
                    User = userId
                };
                
                // Get AI response (it will automatically call functions as needed)
                var response = await _chatService.GetChatMessageContentAsync(
                    chatHistory,
                    executionSettings,
                    _kernel);
                
                // Extract metadata from plugin states
                var detectedMood = _musicPlugin.GetLastDetectedMood();
                var recommendations = _musicPlugin.GetLastRecommendations();
                var pendingPayment = _paymentPlugin.GetPendingPayment(userId);
                
                return new AgentResponse(
                    Message: response.Content ?? "I'm here to help with music!",
                    DetectedMood: detectedMood,
                    RecommendedTracks: recommendations,
                    PaymentIntent: pendingPayment,
                    RequiresPayment: pendingPayment != null
                );
            }
            catch (Exception ex)
            {
                return new AgentResponse(
                    Message: $"Sorry, I encountered an error: {ex.Message}. Please try again!",
                    RequiresPayment: false
                );
            }
        }
        
        public async Task<AgentResponse> ChatWithHistoryAsync(
            string userId, 
            string userMessage,
            List<(string Role, string Content)> conversationHistory)
        {
            try
            {
                var chatHistory = new ChatHistory();
                chatHistory.AddSystemMessage(GetSystemPrompt());
                
                // Add conversation history
                foreach (var (role, content) in conversationHistory)
                {
                    if (role.ToLower() == "user")
                        chatHistory.AddUserMessage(content);
                    else if (role.ToLower() == "assistant")
                        chatHistory.AddAssistantMessage(content);
                }
                
                // Add new user message
                chatHistory.AddUserMessage(userMessage);
                
                var executionSettings = new OpenAIPromptExecutionSettings
                {
                    ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                    Temperature = 0.7,
                    MaxTokens = 500,
                    User = userId
                };
                
                var response = await _chatService.GetChatMessageContentAsync(
                    chatHistory,
                    executionSettings,
                    _kernel);
                
                var detectedMood = _musicPlugin.GetLastDetectedMood();
                var recommendations = _musicPlugin.GetLastRecommendations();
                var pendingPayment = _paymentPlugin.GetPendingPayment(userId);
                
                return new AgentResponse(
                    Message: response.Content ?? "Let's talk about music!",
                    DetectedMood: detectedMood,
                    RecommendedTracks: recommendations,
                    PaymentIntent: pendingPayment,
                    RequiresPayment: pendingPayment != null
                );
            }
            catch (Exception ex)
            {
                return new AgentResponse(
                    Message: $"Oops! Something went wrong: {ex.Message}",
                    RequiresPayment: false
                );
            }
        }
    }
}
