using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;

namespace MusicAI.Agents.Plugins
{
    public class UserPlugin
    {
        private readonly object _usersRepo;
        
        public UserPlugin(object usersRepository)
        {
            _usersRepo = usersRepository ?? throw new ArgumentNullException(nameof(usersRepository));
        }
        
        [KernelFunction("get_user_info")]
        [Description("Get user's profile information including email, country, and subscription status")]
        public async Task<string> GetUserInfoAsync(
            [Description("The user's ID")] string userId)
        {
            try
            {
                var method = _usersRepo.GetType().GetMethod("GetById");
                if (method == null) return "User info not available";
                
                var user = method.Invoke(_usersRepo, new object[] { userId });
                if (user == null) return "User not found";
                
                var email = user.GetType().GetProperty("Email")?.GetValue(user);
                var country = user.GetType().GetProperty("Country")?.GetValue(user);
                var isSubscribed = user.GetType().GetProperty("IsSubscribed")?.GetValue(user);
                var credits = user.GetType().GetProperty("Credits")?.GetValue(user);
                
                return $"User: {email}\nCountry: {country}\nSubscribed: {isSubscribed}\nCredits: {credits}";
            }
            catch (Exception ex)
            {
                return $"Error getting user info: {ex.Message}";
            }
        }
        
        [KernelFunction("get_user_credits")]
        [Description("Get the user's current credit balance")]
        public string GetUserCredits(
            [Description("The user's ID")] string userId)
        {
            try
            {
                var method = _usersRepo.GetType().GetMethod("GetById");
                if (method == null) return "0";
                
                var user = method.Invoke(_usersRepo, new object[] { userId });
                if (user == null) return "0";
                
                var credits = user.GetType().GetProperty("Credits")?.GetValue(user);
                return credits?.ToString() ?? "0";
            }
            catch
            {
                return "0";
            }
        }
    }
}
