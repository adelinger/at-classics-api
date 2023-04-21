﻿using System;
using System.Net.Http.Headers;
using api_at_shop.Model;
using api_at_shop.Model.Products;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using api_at_shop.DTO.Printify.Data.Image;
using api_at_shop.DTO.Printify.Data.Variant;
using api_at_shop.DTO.Printify.Data;
using api_at_shop.DTO.Printify.Data.Option;
using api_at_shop.Services.ProductServices;
using api_at_shop.Model.Shipping;
using api_at_shop.DTO.Printify.Shipping;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Text;
using System.Text.Json.Nodes;
using Azure;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using api_at_shop.Utils.Constants;
using api_at_shop.DTO.Printify;
using api_at_shop.Repository.Entities;
using api_at_shop.Repository;
using api_at_shop.Repository.Entites;
using api_at_shop.Services.common.EmailServices;
using api_at_shop.Model.Email;
using api_at_shop.Utils.Data;
using System.Globalization;
using api_at_shop.Services.ProductServices.Common;
using Microsoft.Extensions.Configuration;

namespace api_at_shop.Services.printify
{
    public class PrintifyService :IProductApiService
    {
        private HttpClient Client;
        private readonly IConfiguration Configuration;
        private readonly string BASE_URL;
        private readonly string TOKEN;
        private readonly ICurrencyService CurrencyService;
        private CurrencyDTO[] Currencies;
        private readonly DataContext DataContext;
        private readonly IEmailService EmailService;
        private readonly string INVOICE_API_URL;
        private readonly ITranslationService TranslationService;

        public PrintifyService(IConfiguration configuration, ICurrencyService currencyService, DataContext dataContext, IEmailService emailService, ITranslationService translationService)
        {
            Client = new HttpClient();
            Configuration = configuration;
            CurrencyService = currencyService;
            BASE_URL = configuration.GetSection("AppSettings").GetSection("PrintifyApiUrl").Value;
            TOKEN = configuration.GetSection("AppSettings").GetSection("PrintifyToken").Value;
            INVOICE_API_URL = configuration.GetSection("AppSettings").GetSection("InvoicesBaseUrl").Value;

            Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TOKEN);
            DataContext = dataContext;
            EmailService = emailService;
            TranslationService = translationService;
        }

        public Task<IProduct> DeleteProductAsync(string id)
        {
            throw new NotImplementedException();
        }

        public async Task<IProduct> GetProductAsync(string id, string languageCulture = null)
        {
            using HttpResponseMessage res = await Client.GetAsync(BASE_URL + "/products/"+id+".json");
            res.EnsureSuccessStatusCode();
            var product = await res.Content.ReadFromJsonAsync<PrintifyData>();
            var availableOptions = GetAvailableOptions(product);
            Currencies = await CurrencyService.GetCurrencies();

            return await GetMappedProductAsync(product, isSingleProduct:true, languageCulture:languageCulture);
        }

        public async Task<ProductData> GetFeaturedProducts(string languageCulture)
        {
            try
            {
                var product = await GetProductsAsync();
                product.Data = product.Data.Where(p => p.Is_Locked == false).ToList();

                Currencies = await CurrencyService.GetCurrencies();

                var mapped = new List<IProduct>();
                var limit = ProductConstants.DefaultFeaturedRecordsPerPage;

                var filtered = product.Data.Where(item => item.Tags.Contains("Featured", StringComparer.InvariantCultureIgnoreCase)).OrderByDescending(item => DateTime.Parse(item.Created_At)).Take(limit).ToList();

                foreach (var item in filtered)
                {
                    mapped.Add(await GetMappedProductAsync(item, false, languageCulture));
                }

                return new ProductData { Product = mapped, rpp = (int)limit, Total = mapped.Count() };
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        public async Task<ProductData> GetRelatedProducts(string productId, int limit, string languageCulture)
        {
            try
            {
                var listOfProducts = await GetProductsAsync();
                var product = listOfProducts.Data.SingleOrDefault(p => p.ID == productId);

                Currencies = await CurrencyService.GetCurrencies();

                var mapped = new List<IProduct>();

                var relatedProduct = SearchSimilarProducts(product, listOfProducts.Data);

                foreach (var item in relatedProduct)
                {
                    mapped.Add(await GetMappedProductAsync(item, false, languageCulture));
                }

                return new ProductData { Product = mapped,
                    rpp = limit == 0 ? ProductConstants.DefeaultRelatedRecordsPerPage : limit, Total = mapped.Count() };
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        static List<PrintifyData> SearchSimilarProducts(PrintifyData variableProduct, List<PrintifyData> products)
        {
            var variableProductTags = variableProduct.Tags.Select(t => t.ToLower());
            return products.Where(p => p.ID != variableProduct.ID)
                          .Where(p => p.Tags.Select(t => t.ToLower()).Intersect(variableProductTags).Any())
                          .OrderByDescending(p => p.Tags.Select(t => t.ToLower()).Intersect(variableProductTags).Count())
                          .ThenBy(p => p.Created_At)
                          .ToList();
        }

        private async Task<PrintifyProductDTO> GetProductsAsync()
        {
            using HttpResponseMessage res = await Client.GetAsync(BASE_URL + "/products.json");
            res.EnsureSuccessStatusCode();

            var product = await res.Content.ReadFromJsonAsync<PrintifyProductDTO>();
            //product.Data = product.Data.Where(p => p.Is_Locked == false).ToList();

            return product;
        }

        public async Task<ProductData> GetProductsAsync(string categoryFilter="", string searchFilter="",
            int? limit = null, string sortOrder="", string tagFilters = "", string languageCulture = null)
        {
            try
            {
                var product = await GetProductsAsync();
                var total = product.Data.Count();

                Currencies = await CurrencyService.GetCurrencies();

                var mapped = new List<IProduct>();

                var filtered = new List<PrintifyData>();
                if (!string.IsNullOrEmpty(categoryFilter))
                {
                    var filterTags = getTags(categoryFilter);

                    filtered = product.Data.Where(item => item.Tags.Any(tag => filterTags.Contains(tag))).ToList();
                }

                if (!string.IsNullOrEmpty(tagFilters) && tagFilters != "undefined")
                {
                    string[] tagFiltersArray = tagFilters.Split(',');
                    if(tagFilters.Contains("Other cars"))
                    {
                        filtered = filtered.Any()
                       ? filtered.Where(item => !item.Tags.Any(tag => CarTypes.CarTypesList.Contains(tag))).ToList()
                       : product.Data.Where(item => !item.Tags.Any(tag => CarTypes.CarTypesList.Contains(tag))).ToList();
                    }
                    else
                    {
                        filtered = filtered.Any()
     ? filtered.Where(item => tagFiltersArray.All(tag => item.Tags.Contains(tag))).ToList()
     : product.Data.Where(item => tagFiltersArray.All(tag => item.Tags.Contains(tag))).ToList();
                    }

                    if (!filtered.Any())
                    {
                        return new ProductData { Product = mapped, rpp = (int)limit, Total = total };
                    }
                }


                foreach (var (item, possibleOptions) in from item in filtered.Any() ? filtered : product?.Data
                                                        let possibleOptions = new List<List<int>>()
                                                        select (item, possibleOptions))
                {
                    possibleOptions.AddRange(from variant in item.Variants
                                             select variant.Options);

                    mapped.Add(await GetMappedProductAsync(item, false, languageCulture));
                }

                if (!string.IsNullOrEmpty(searchFilter))
                {
                    mapped = mapped.Where(item => item.Title.ToLower().Contains(searchFilter.ToLower())).ToList();
                    
                }

                if (!string.IsNullOrEmpty(sortOrder))
                {
                    mapped = OrderBy(sortOrder, mapped);
                }
                else
                {
                    //default sort order is by newest added products
                    mapped = mapped.OrderByDescending(item => DateTime.Parse(item.Created_At)).ToList();
                }

                total = mapped.Count;

                if (limit != null)
                {
                    mapped = mapped.Take((int)limit).ToList();
                }
                return new ProductData { Product = mapped, rpp = (int)limit, Total = total };
            }
            catch (Exception ex)
            {
                throw ex;
            }
            
        }

        private async Task<IProduct> GetMappedProductAsync(PrintifyData item, bool isSingleProduct = false, string languageCulture = null)
        {
            var availableOptions = GetAvailableOptions(item);
            var availableColors = availableOptions.Colors;
            var defaultImages = GetDefaultImages(item);
            var lowestPriceUsd = item.Variants.Where(v=>v.Is_Enabled == true).OrderBy(p => p.Price).FirstOrDefault().Price;
            var variants = isSingleProduct ? await GetMappedVariants(item.Variants) : null;
            var defaultVariantID = item.Variants.Find(e => e.Is_Default == true).ID;

            var translated = new ProductTranslation();
            if(languageCulture != null)
            {
                try
                {
                    translated = await TranslationService.GetProductTranslationAsync(item.ID, languageCulture);
                }
                catch (Exception ex)
                {
                    translated = null;
                }
            }

            var title = languageCulture != null && translated != null ? translated.ProductTitle : item.Title;
            var description = languageCulture != null && translated != null ? translated.ProductDescription : item.Description;

            var price = await CurrencyService.ConvertUsdToEur(lowestPriceUsd, Currencies);
            return (new Product
            {
                ID = item.ID,
                Created_At = item.Created_At,
                Description = description,
                Is_Locked = item.Is_Locked,
                Tags = item.Tags,
                Title = title,
                Updated_At = item.Updated_At,
                Visible = item.Visible,
                AvailableColors = availableColors,
                AvailableSizes = availableOptions.Sizes,
                FeaturedImageSrc = defaultImages?.FirstOrDefault()?.Src,
                Images = GetMappedImages(item),
                DefaultImages = defaultImages,
                Variants = variants,
                Options = isSingleProduct ? GetMappedOptions(item.Options, variants) : null,
                IsDiscounted = false,
                lowestPrice = price != 0 ? price : lowestPriceUsd,
                Currency = price != 0 ? "€" : "$",
                DefaultVariantID = defaultVariantID,
            });

        }

        private List<IProduct> OrderBy(string sortOrder, List<IProduct> data)
        {
            switch (sortOrder)
            {
                case "createdAsc":
                    data = data.OrderBy(a => DateTime.Parse(a.Created_At)).ToList();
                    break;
                case "createdDesc":
                    data = data.OrderByDescending(a => DateTime.Parse(a.Created_At)).ToList();
                    break;

                case "updatedAsc":
                    data = data.OrderBy(a => DateTime.Parse(a.Updated_At)).ToList();
                    break;

                case "updatedDesc":
                    data = data.OrderByDescending(a => DateTime.Parse(a.Updated_At)).ToList();
                    break;
                case "priceAsc":
                    data = data.OrderBy(a => a.lowestPrice).ToList();
                    break;

                case "priceDesc":
                    data = data.OrderByDescending(a =>a.lowestPrice).ToList();
                    break;

                default:
                    data = data.OrderByDescending(a => DateTime.Parse(a.Created_At)).ToList();
                    break;
            }
            return data;
        }

        public Task<IProduct> UpdateProductAsync(IProduct product)
        {
            throw new NotImplementedException();
        }

        private List<ProductImage> GetDefaultImages(PrintifyData item)
        {
            var defaultImages = new List<ProductImage>();
            var defaultVariant = item.Variants.FirstOrDefault(e => e.Is_Default == true);
            foreach (var image in item.Images)
            {
                int index = image.Variant_Ids.FindIndex(item => item == defaultVariant?.ID);
                if (index >= 0)
                {
                    defaultImages.Add(new ProductImage
                    {
                        Src = image.Src,
                        Is_Default = image.Is_Default,
                        Is_Selected_For_Publishing = image.Is_Selected_For_Publishing,
                        Variant_Ids = image.Variant_Ids,
                        Position = image.Position,
                    });
                }
            }

            return defaultImages;
        }

        private List<List<int>> GetPossibleOptions(PrintifyData item)
        {
            var possibleOptions = new List<List<int>>();
            foreach (var variant in item.Variants)
            {
                if (variant.Is_Enabled)
                {
                    possibleOptions.Add(variant.Options);
                }
            }

            return possibleOptions;
        }     

        private ProductOptions GetAvailableOptions(PrintifyData item)
        {
            var options = new ProductOptions();
            var availableSizes = new List<ProductSize>();
            var availableColors = new List<ProductColor>();
            var possibleOptions = GetPossibleOptions(item);

                foreach (var option in item.Options)
                {
                        foreach (var value in option.Values)
                        {
                            foreach (var possibleOption in possibleOptions)
                            {
                                int index = possibleOption.FindIndex(item => item == value.ID);
                                if (index >= 0)
                                {
                            if (option.Name == "Colors")
                            {
                                int hasIndex = availableColors.FindIndex(item => item.ID == value.ID);
                                if (hasIndex < 0)
                                {
                                    availableColors.Add(new ProductColor
                                    {
                                        ID = value.ID,
                                        Colors = value.Colors,
                                        Title = value.Title
                                    });
                                }
                            }
                            else if (option.Name == "Sizes")
                            {
                                int hasIndex = availableSizes.FindIndex(item => item.ID == value.ID);
                                if (hasIndex < 0)
                                {
                                    availableSizes.Add(new ProductSize
                                    {
                                        ID = value.ID,
                                        Colors = value.Colors,
                                        Title = value.Title
                                    });
                                }
                            }
                                    
                                }
                            }
                        } 
                
                }
            options.Sizes = availableSizes;
            options.Colors = availableColors;
            return options;
        }

        private List<ProductImage> GetMappedImages(PrintifyData data)
        {
            try
            {
                var mapped = new List<ProductImage>();


                foreach (var image in data.Images)
                {
                    var match = data.Variants?.FirstOrDefault(e => e.ID == image.Variant_Ids?.FirstOrDefault())?.Options;
                    var colors = data.Options?.Where(e => e.Name == "Colors").FirstOrDefault();
                    var printifyColor = new PrintifyValues();
                    foreach (var item in match)
                    {
                        var find = colors?.Values?.Where(e => e.ID == item);
                        if(find != null && find.Any())
                        {
                            printifyColor = find.FirstOrDefault();
                            break;
                        }
                    }

                    var colorMatch = new ProductColor
                    {
                        Colors = printifyColor?.Colors,
                        ID = printifyColor.ID,
                        Title = printifyColor?.Title,
                    };

                    mapped.Add(new ProductImage
                    {
                        Is_Default = image.Is_Default,
                        Is_Selected_For_Publishing = image.Is_Selected_For_Publishing,
                        Position = image.Position,
                        Src = image.Src,
                        Variant_Ids = image.Variant_Ids,
                        ColorID = colorMatch.ID
                    });
                }

                return mapped;
            }
            catch (Exception ex)
            {
                throw ex;
            }
           
        }

        private List<ProductOptions> GetMappedOptions(List<PrintifyOptions> printifyOptions, List<ProductVariant> Variants)
        {
            var mapped = new List<ProductOptions>();

            foreach (var option in printifyOptions)
            {
                var list = Variants.Select(v => v.Options);
                var ids = new List<int>();
                foreach (var variant in list)
                {
                    foreach (var item in variant)
                    {
                        ids.Add(item);
                    }
                }

                mapped.Add(new ProductOptions
                {
                    Name = option.Name,
                    Type = option.Type,
                    Values = option.Values.Where(o=> ids.Contains(o.ID)).ToList()
                });
            }

            return mapped;
        }

        private List<string> getTags(string filter)
        {
            List<string> tags = new List<string>();

            if (filter == "clothing")
            {
                tags.Add("Men's Clothing");
                tags.Add("Women's Clothing");
                tags.Add("Kids' Clothing");
                return tags;
            }

            if (filter == "accessories")
            {
                tags.Add("Accessories");
                return tags;
            }
            if(filter == "home-and-living")
            {
                tags.Add("Home & Living");
                return tags;
            }

            return tags;
        }

        private async Task<List<ProductVariant>> GetMappedVariants(List<PrintifyVariants> printifyVariant)
        {
            try
            {

                var mapped = new List<ProductVariant>();

                foreach (var variant in printifyVariant)
                {
                    var price = await CurrencyService.ConvertUsdToEur(variant.Price, Currencies);
                    //int price = 0;
                    if (variant.Is_Enabled)
                    {
                        mapped.Add(new ProductVariant
                        {
                            Grams = variant.Grams,
                            ID = variant.ID,
                            Is_Available = variant.Is_Available,
                            Is_Default = variant.Is_Default,
                            Is_Enabled = variant.Is_Enabled,
                            sku = variant.sku,
                            Options = variant.Options,
                            Price = price != 0 ? price : variant.Price,
                            Quantity = variant.Quantity,
                            Title = variant.Title
                        });
                    }

                }

                return mapped;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        public async Task<object> GetShippingPrice(IShippingInformation ShippingInformation)
        {
            try
            {
                if (!IsEuCountry(ShippingInformation.address_to.country))
                {
                    throw new Exception("Country not supported");
                }

                var serilaizeJson = JsonConvert.SerializeObject(ShippingInformation, Formatting.Indented,
                    new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore,
                        ContractResolver = new CamelCasePropertyNamesContractResolver()
                    });

                var content = new StringContent(serilaizeJson.ToString(), Encoding.UTF8, "application/json");
                using HttpResponseMessage res = await Client.PostAsync(BASE_URL + "/orders/shipping.json", content);
                res.EnsureSuccessStatusCode();

                return await res.Content.ReadFromJsonAsync<object>();
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        public async Task<Model.Response> AddTagAsync(string id, string tag)
        {
            return await AddRemoveTag("add", id, tag);
        }

        public async Task<Model.Response> RemoveTagAsync(string id, string tag)
        {
            return await AddRemoveTag("remove", id, tag);
        }

        private async Task<Model.Response> AddRemoveTag(string addOrRemove, string id, string tag)
        {
            var response = new Model.Response();
            using HttpResponseMessage res = await Client.GetAsync(BASE_URL + "/products/" + id + ".json");
            res.EnsureSuccessStatusCode();
            var product = await res.Content.ReadFromJsonAsync<PrintifyData>();

            var match = product.Tags.Any(t => t == tag);

            if(match && addOrRemove == "remove")
            {
                product.Tags.Remove(tag);
            }

            if (addOrRemove == "add" && match)
            {
                response.Message = "Tag already exists";
                response.Success = false;
                return response;
            }

            if(addOrRemove == "add" && !match)
            {
                product.Tags.Add(tag);
            }

            Tags tags = new Tags();
            tags.tags = product.Tags;

            using HttpResponseMessage req = await Client.PutAsJsonAsync(BASE_URL + "/products/" + id + ".json", tags);

            req.EnsureSuccessStatusCode();

            response.Success = req.StatusCode == System.Net.HttpStatusCode.OK ? true : false;
            

            return response;
        }

        public bool IsEuCountry(string country)
        {
            var euCountries = new List<string>
            {
                          "AT","BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
                          "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
                          "PL", "PT", "RO", "SK", "SI", "ES", "SE",

            };

            return euCountries.Contains(country);
        }

        private async Task<int> GetVariantPrice(string productID, int? variantID)
        {
            try
            {
                var product = await GetProductAsync(productID);
                var variant = product.Variants.SingleOrDefault(v => v.ID == variantID);

                return variant.Price;
            }
            catch (Exception ex)
            {
                return 0;
            }
        }

        public async Task<Order> MakeNewOrder(IShippingInformation OrderDetails)
        {
            try
            {
                OrderDetails.external_id = Guid.NewGuid().ToString();
                OrderDetails.label = "at shop order";
                var serilaizeJson = JsonConvert.SerializeObject(OrderDetails, Formatting.Indented,
                   new JsonSerializerSettings
                   {
                       NullValueHandling = NullValueHandling.Ignore,
                       ContractResolver = new CamelCasePropertyNamesContractResolver()
                   });

                var content = new StringContent(serilaizeJson.ToString(), Encoding.UTF8, "application/json");
                using HttpResponseMessage res = await Client.PostAsync(BASE_URL + "/orders.json", content);
                res.EnsureSuccessStatusCode();

                var result = await res.Content.ReadFromJsonAsync<Order>();
                result.Success = res.StatusCode == System.Net.HttpStatusCode.OK ? true : false;


                if (result.Success)
                {
                    var mapped = GetMappedOrderObject(OrderDetails, result.ID);

                    foreach (var item in OrderDetails.line_items)
                    {
                        var product = new ProductOrderEntity
                        {
                            PrintProviderID = item.Print_provider_id,
                            BluePrintID = item.Blueprint_id,
                            VariantID = item.Variant_id,
                            PrintifyProductID = item.Product_id,
                            Quantity = item.Quantity,
                            Sku = item.Sku,
                            Order = mapped
                        };
                        var addProduct = await DataContext.AddAsync<ProductOrderEntity>(product);
                    }
                    var task = await DataContext.AddAsync<OrderEntity>(mapped);
                    await DataContext.SaveChangesAsync();
                }

                var sendEmail = await EmailService.SendOrderConfirmEmail(OrderDetails, result.ID);

                //TODO: remove fixed email when invoice sending solution is resolved
                OrderDetails.address_to.email = "antun994@gmail.com";
                var sendInvoice = await CreateInvoiceAsync(OrderDetails);

                return result;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        private OrderEntity GetMappedOrderObject(IShippingInformation OrderDetails, string printifyOrderId)
        {
            return new OrderEntity
            {
                ExternalID = OrderDetails.external_id,
                Label = OrderDetails.label,
                PrintifyID = printifyOrderId,
                Address1 = OrderDetails.address_to.address1,
                Address2 = OrderDetails.address_to.address2,
                FirstName = OrderDetails.address_to.first_name,
                LastName = OrderDetails.address_to.last_name,
                Email = OrderDetails.address_to.email,
                City = OrderDetails.address_to.city,
                Phone = OrderDetails.address_to.phone,
                Country = OrderDetails.address_to.country,
                Region = OrderDetails.address_to.region,
                Zip = OrderDetails.address_to.zip,
                SendShippingInformation = OrderDetails.send_shipping_notification,
                ShippingMethod = OrderDetails.shipping_method,
                TimeStamp = DateTime.UtcNow.AddHours(1),
            };
        }

        private async Task<bool> IsPriceValid(IShippingInformation OrderDetails)
        {
            var shipping = await GetShippingPrice(OrderDetails);
            dynamic shippingCostsObject = JsonConvert.DeserializeObject<dynamic>(shipping.ToString());
            int shippingCost = shippingCostsObject[OrderDetails.shipping_method == 1 ? "standard" : "express"];
            var itemPrices = new List<int>();
            foreach (var item in OrderDetails.line_items)
            {
                var variantPrice = await GetVariantPrice(item.Product_id, item.Variant_id);
                itemPrices.Add(item.Quantity * variantPrice);
            }
            var totalCost = GetPriceInString(shippingCost + itemPrices.Sum());
            var orderCost = OrderDetails.totalPrice.Remove(OrderDetails.totalPrice.Length-1);
            return AreEqualWithDeviation(totalCost, orderCost, 0.1);
        }

        private static string GetPriceInString(int number)
        {
            var str = number.ToString();
            var resStr = str.Substring(0, str.Length - 2) + "." + str.Substring(str.Length - 2);
            return resStr;
        }

        private static bool AreEqualWithDeviation(string a, string b, double deviation)
        {
            decimal num1, num2;
            if (decimal.TryParse(a, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out num1) &&
            decimal.TryParse(b, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out num2))
            {
                decimal difference = Math.Abs(num1 - num2);
                decimal maximumDifference = Math.Max(Math.Abs(num1), Math.Abs(num2)) * (decimal)deviation;
                return difference <= maximumDifference;
            }
            else
            {
                return false;
            }
        }

        public async Task<bool> IsOrderValid(IShippingInformation OrderDetails)
        {
            try
            {   
                return await IsPriceValid(OrderDetails); 
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        public async Task<string> CreateInvoiceAsync(IShippingInformation OrderDetails)
        {
            try
            {

                var invoice = new Invoice
                {
                    Address1 = OrderDetails.address_to.address1,
                    Address2 = OrderDetails.address_to.address2,
                    City = OrderDetails.address_to.city,
                    Country = OrderDetails.address_to.country,
                    Region = OrderDetails.address_to.region,
                    Phone = OrderDetails.address_to.phone,
                    FirstName = OrderDetails.address_to.first_name,
                    LastName = OrderDetails.address_to.last_name,
                    InvoiceNumber = OrderDetails.external_id,
                    Email = OrderDetails.address_to.email,
                    InvoiceDate = DateTime.UtcNow.AddHours(1),
                    InvoiceTimeStamp = DateTime.UtcNow.AddHours(1).ToString(),
                    PaymentType = "Paypal",
                    Zip = OrderDetails.address_to.zip,
                    OperatorName = "Webshop",
                    ShippingPrice = decimal.Parse(OrderDetails.shipping_price.Remove(OrderDetails.shipping_price.Length - 1), CultureInfo.InvariantCulture)
                };

                invoice.ListOfItems = new List<InvoiceItem>();
                foreach (var item in OrderDetails.line_items)
                {
                    var price = decimal.Parse(item.Price.Remove(item.Price.Length - 1), CultureInfo.InvariantCulture);

                    invoice.ListOfItems.Add(new InvoiceItem
                    {
                        Price = price,
                        Quantity = item.Quantity,
                        Title = item.Title
                    });
                }

                var serilaizeJson = JsonConvert.SerializeObject(invoice, Formatting.Indented,
                  new JsonSerializerSettings
                  {
                      NullValueHandling = NullValueHandling.Ignore,
                      ContractResolver = new CamelCasePropertyNamesContractResolver()
                  });

                var username = Configuration.GetSection("AppSettings").GetSection("InvoicesApiUsername").Value;
                var password = Configuration.GetSection("AppSettings").GetSection("InvoicesApiPassword").Value;
                Client = new HttpClient();
                Client.BaseAddress = new Uri(INVOICE_API_URL);
                Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}")));
                var content = new StringContent(serilaizeJson.ToString(), Encoding.UTF8, "application/json");
                using HttpResponseMessage res = await Client.PostAsync(INVOICE_API_URL + "/create-invoice", content);
                res.EnsureSuccessStatusCode();

                return await res.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                return "";
            }
        }
    }

    internal class Tags
    {
        public List<string>? tags { get; set; }
    }
}

