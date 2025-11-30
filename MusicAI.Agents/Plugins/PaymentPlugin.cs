using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using MusicAI.Agents.Models;

namespace MusicAI.Agents.Plugins
{
    public class PaymentPlugin
    {
        private readonly object _packagesRepo;
        private readonly object _subsRepo;
        private readonly Dictionary<string, PaymentIntent> _pendingPayments = new();
        
        public PaymentPlugin(object creditPackagesRepository, object subscriptionsRepository)
        {
            _packagesRepo = creditPackagesRepository ?? throw new ArgumentNullException(nameof(creditPackagesRepository));
            _subsRepo = subscriptionsRepository ?? throw new ArgumentNullException(nameof(subscriptionsRepository));
        }
        
        [KernelFunction("get_credit_packages")]
        [Description("Get all available credit packages that users can purchase. Shows prices and credits.")]
        public async Task<string> GetPackagesAsync()
        {
            try
            {
                var method = _packagesRepo.GetType().GetMethod("GetActivePackagesAsync");
                if (method == null) return "Credit packages not available";
                
                var task = (Task<dynamic>)method.Invoke(_packagesRepo, null);
                var packages = await task;
                
                var packageList = string.Join("\n", (packages as IEnumerable<object> ?? new List<object>()).Select(p =>
                {
                    var name = p.GetType().GetProperty("Name")?.GetValue(p);
                    var price = p.GetType().GetProperty("PriceNgn")?.GetValue(p);
                    var credits = p.GetType().GetProperty("Credits")?.GetValue(p);
                    var id = p.GetType().GetProperty("Id")?.GetValue(p);
                    var days = p.GetType().GetProperty("DurationDays")?.GetValue(p);
                    return $"- {name}: ₦{price} for {credits} credits (valid for {days} days) [ID: {id}]";
                }));
                
                return $"Available Credit Packages:\n{packageList}";
            }
            catch (Exception ex)
            {
                return $"Error loading packages: {ex.Message}";
            }
        }
        
        [KernelFunction("check_user_subscription")]
        [Description("Check if a user has an active subscription and how many credits they have remaining")]
        public async Task<string> CheckSubscriptionAsync(
            [Description("The user's ID")] string userId)
        {
            try
            {
                var method = _subsRepo.GetType().GetMethod("GetActiveSubscriptionAsync");
                if (method == null) return "Subscription check not available";
                
                var task = (Task<dynamic>)method.Invoke(_subsRepo, new object[] { userId });
                var subscription = await task;
                
                if (subscription == null)
                {
                    return "User has no active subscription. Trial credits may be available.";
                }
                
                var credits = subscription.GetType().GetProperty("CreditsRemaining")?.GetValue(subscription);
                var expires = (DateTime?)subscription.GetType().GetProperty("ExpiresAt")?.GetValue(subscription);
                var status = subscription.GetType().GetProperty("Status")?.GetValue(subscription);
                
                var daysRemaining = expires.HasValue ? (int)(expires.Value - DateTime.UtcNow).TotalDays : 0;
                
                return $"Subscription Status: {status}\nCredits Remaining: {credits}\nExpires in: {daysRemaining} days ({expires:yyyy-MM-dd})";
            }
            catch (Exception ex)
            {
                return $"Error checking subscription: {ex.Message}";
            }
        }
        
        [KernelFunction("recommend_package")]
        [Description("Recommend a credit package based on how often the user listens to music")]
        public async Task<string> RecommendPackageAsync(
            [Description("How often user listens: 'daily', 'weekly', or 'occasionally'")] string usagePattern)
        {
            try
            {
                var method = _packagesRepo.GetType().GetMethod("GetActivePackagesAsync");
                if (method == null) return "Packages not available";
                
                var task = (Task<dynamic>)method.Invoke(_packagesRepo, null);
                var packages = await task;
                
                var packageList = new List<object>(packages as IEnumerable<object> ?? new List<object>());
                
                var recommended = usagePattern.ToLower() switch
                {
                    "daily" or "everyday" or "frequently" => packageList.FirstOrDefault(p => 
                        p.GetType().GetProperty("Id")?.GetValue(p)?.ToString() == "pkg_premium"),
                    "weekly" or "regular" or "sometimes" => packageList. FirstOrDefault(p => 
                        p.GetType().GetProperty("Id")?.GetValue(p)?.ToString() == "pkg_standard"),
                    _ => packageList.FirstOrDefault(p => 
                        p.GetType().GetProperty("Id")?.GetValue(p)?.ToString() == "pkg_basic")
                };
                
                if (recommended == null) return "Unable to recommend a package";
                
                var name = recommended.GetType().GetProperty("Name")?.GetValue(recommended);
                var price = recommended.GetType().GetProperty("PriceNgn")?.GetValue(recommended);
                var credits = recommended.GetType().GetProperty("Credits")?.GetValue(recommended);
                
                return $"I recommend the {name} package: ₦{price} for {credits} credits. This is perfect for {usagePattern} listeners!";
            }
            catch (Exception ex)
            {
                return $"Error recommending package: {ex.Message}";
            }
        }
        
        [KernelFunction("get_package_details")]
        [Description("Get detailed information about a specific credit package by ID")]
        public async Task<string> GetPackageDetailsAsync(
            [Description("The package ID like 'pkg_basic', 'pkg_standard', or 'pkg_premium'")] string packageId)
        {
            try
            {
                var method = _packagesRepo.GetType().GetMethod("GetByIdAsync");
                if (method == null) return "Package details not available";
                
                var task = (Task<dynamic>)method.Invoke(_packagesRepo, new object[] { packageId });
                var package = await task;
                
                if (package == null) return $"Package '{packageId}' not found";
                
                var name = package.GetType().GetProperty("Name")?.GetValue(package);
                var price = package.GetType().GetProperty("PriceNgn")?.GetValue(package);
                var credits = package.GetType().GetProperty("Credits")?.GetValue(package);
                var days = package.GetType().GetProperty("DurationDays")?.GetValue(package);
                
                return $"Package: {name}\nPrice: ₦{price}\nCredits: {credits}\nDuration: {days} days\nID: {packageId}";
            }
            catch (Exception ex)
            {
                return $"Error getting package details: {ex.Message}";
            }
        }
        
        public PaymentIntent? GetPendingPayment(string userId)
        {
            _pendingPayments.TryGetValue(userId, out var intent);
            return intent;
        }
        
        public void SetPendingPayment(string userId, PaymentIntent intent)
        {
            _pendingPayments[userId] = intent;
        }
    }
}
