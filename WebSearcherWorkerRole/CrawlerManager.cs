using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using WebSearcherCommon;

namespace WebSearcherWorkerRole
{
    internal class CrawlerManager
    {

        internal static async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    string url = await StorageManager.RetrieveCrawleRequestAsync(cancellationToken);
                    if (!string.IsNullOrEmpty(url))
                    {
                        if (!(await CrawleOneAsync(url, cancellationToken)))
                        {
                            // fail requeue the URL en P5
                            await StorageManager.StoreErrorCrawleRequestAsync(url, cancellationToken);
                        }
                    }
                    else // empty queue
                    {
#if DEBUG
                        await Task.Delay(20000, cancellationToken);
#else
                        await Task.Delay(60000, cancellationToken);
#endif
                    }
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Trace.TraceError("CrawlerManager.RunAsync Exception : " + ex.GetBaseException().ToString());
#if DEBUG
                if (Debugger.IsAttached) { Debugger.Break(); }
#endif
            }
        }

        private static char[] forbiddenInHd = { '&', '\\', '\'', '\"', '\0', '\b', '\n', '\r', '\t', (char)26, '%', '_' };

        private static object concurrentHashSetLock = new object();
        /// <summary>
        /// Avoid to push several time the same URL to the queue during the lifetime of the worker ~1 h (see settings)
        /// </summary>
        private static HashSet<string> concurrentHashSet = new HashSet<string>();
        internal static void SafeAddConcurrentHashSet(string url)
        {
            lock (concurrentHashSetLock)
            {
                if (!concurrentHashSet.Contains(url))
                {
                    concurrentHashSet.Add(url);
                }
            }
        }
        
        internal static async Task<bool> CrawleOneAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                Uri uriOrig = new Uri(url); // url have been Normalized before push in the queue
                
                PageEntity page = new PageEntity(uriOrig);

                using (SqlManager sql = new SqlManager())
                {
                    // last scan date ?
                    if (!await sql.CheckIfCanCrawlePageAsync(url, page.HiddenService, cancellationToken))
                    {
                        return true; // don't refresh !
                    }

                    string rawHtml;
                    try
                    {
                        using (ProxyManager proxy = new ProxyManager())
                        {
                            rawHtml = await proxy.DownloadStringTaskAsync(uriOrig); // TO BIZ DON't ALLOW binary data !!
                        }
                    }
                    catch (WebException ex)
                    {
                        bool isRetry = true;
                        if (ex.Status != WebExceptionStatus.RequestCanceled)
                        {
                            if (ex.Response is HttpWebResponse err)
                            {
                                if (err.StatusCode != HttpStatusCode.NotFound)
                                {
                                    page.InnerText = err.StatusDescription; // won't be saved by PageInsertOrUpdateKo anaway...
                                }
                                else if (url != page.HiddenService) // 404 cleanup execpt domain root (who replied obviously)
                                {
                                    isRetry = false;
                                }
                            }
                            else
                            {
                                Trace.TraceWarning("CrawlerManager.CrawleOneAsync DownloadStringTaskAsync " + url + " : Error " + ex.GetBaseException().Message);
                            }
                        }
                        else // raise by ProxyManager_DownloadProgressChanged
                        {
                            Trace.TraceInformation("CrawlerManager.CrawleOneAsync DownloadStringTaskAsync " + url + " : Cancelled");
                            isRetry = false;
                        }
                        if(isRetry)
                        {
                            await sql.PageInsertOrUpdateKo(page, cancellationToken);
                            return false;
                        }
                        else
                        {
                            await sql.UrlPurge(url, cancellationToken);
                            return true; // looks like an OK for the manager : he won't retry
                        }
                    }
                    
                    HtmlDocument htmlDoc = new HtmlDocument();
                    htmlDoc.LoadHtml(rawHtml);
#if SAVEHTMLRAW
                    page.HtmlRaw = rawHtml;
#endif
                    rawHtml = null;
                    htmlDoc.DocumentNode.Descendants("script").ToList().ForEach(x => x.Remove()); // else appear in InnerText !
                    htmlDoc.DocumentNode.Descendants("style").ToList().ForEach(x => x.Remove()); // else appear in InnerText !
                    htmlDoc.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Comment).ToList().ForEach(x => x.Remove()); // else appear in InnerText !

                    // Title
                    HtmlNode htmlNode2 = htmlDoc.DocumentNode.SelectSingleNode("//head/title");
                    if (!string.IsNullOrEmpty(page.InnerText))
                        page.Title = PageEntity.NormalizeText(HttpUtility.HtmlDecode(htmlNode2.InnerText));
                    else
                        page.Title = uriOrig.Host;
                    // InnerText
                    htmlNode2 = htmlDoc.DocumentNode.SelectSingleNode("//body");
                    if (!string.IsNullOrEmpty(page.InnerText))
                        page.InnerText = PageEntity.NormalizeText(HttpUtility.HtmlDecode(htmlNode2.InnerText));
                    else
                        page.InnerText = string.Empty; // null will raise an exception on the sql proc call
                    htmlNode2 = null;

                    // <A href digest
                    HashSet<string>  innerLinks = new HashSet<string>();
                    HashSet<string>  outerLinks = new HashSet<string>();
                    foreach (HtmlNode htmlNode in htmlDoc.DocumentNode.Descendants("a"))
                    {
                        if (htmlNode.Attributes.Contains("href") && !cancellationToken.IsCancellationRequested)
                        {
                            string href = htmlNode.Attributes["href"].Value;

                            Uri uriResult = null;
                            if (href.StartsWith("http"))
                            {
                                if (Uri.TryCreate(href, UriKind.Absolute, out uriResult) && uriResult.IsTor())
                                {
                                    string str = uriResult.ToString();
                                    str = PageEntity.NormalizeUrl(str);

                                    if (uriResult.DnsSafeHost != uriOrig.DnsSafeHost)
                                    {
                                        if (!outerLinks.Contains(str) && !concurrentHashSet.Contains(str))
                                        {
                                            outerLinks.Add(str);
                                            SafeAddConcurrentHashSet(str);
                                        }
                                    }
                                    else
                                    {
                                        if (!innerLinks.Contains(str) && !concurrentHashSet.Contains(str))
                                        {
                                            innerLinks.Add(str);
                                            SafeAddConcurrentHashSet(str);
                                        }
                                    }
                                }
                            }
                            else if (!href.StartsWith("#"))
                            {
                                if (Uri.TryCreate(uriOrig, href, out uriResult) && uriResult.IsTor())
                                {
                                    string str = uriResult.ToString();
                                    str = PageEntity.NormalizeUrl(str);

                                    if (!innerLinks.Contains(str) && !concurrentHashSet.Contains(str))
                                    {
                                        innerLinks.Add(str);
                                        SafeAddConcurrentHashSet(str);
                                    }
                                }
                            }
                        }
                    }
                    htmlDoc = null;

                    // finish page object before save
                    if (innerLinks.Contains(page.Url))
                        innerLinks.Remove(page.Url);
                    if (innerLinks.Contains(page.HiddenService))
                        innerLinks.Remove(page.HiddenService);

                    page.OuterHdLinks = new HashSet<string>();
                    // Ask to follow
                    foreach (string str in outerLinks)
                    {
                        string hd = PageEntity.GetHiddenService(str);
                        if (!cancellationToken.IsCancellationRequested && await sql.CheckIfCanCrawlePageAsync(str, hd, cancellationToken))
                            await StorageManager.StoreOuterCrawleRequestAsync(str, (hd == str), cancellationToken);
                        if (!page.OuterHdLinks.Contains(hd))
                        {    // basic check for sql injection
                            if (hd.Length <= 37 && hd.IndexOfAny(forbiddenInHd) == -1)
                                page.OuterHdLinks.Add(hd);
                            else
                            {
                                Trace.TraceWarning("CrawlerManager.CrawleOneAsync Strange HD outer link from " + url + " : " + hd);
#if DEBUG
                                if (Debugger.IsAttached) { Debugger.Break(); }
#endif
                            }
                        }
                    }
                    outerLinks = null;
                    bool isLinkFromRoot = (page.HiddenService == page.Url);
                    foreach (string str in innerLinks)
                    {
                        if (!cancellationToken.IsCancellationRequested && await sql.CheckIfCanCrawlePageAsync(str, page.HiddenService, cancellationToken))
                            await StorageManager.StoreInnerCrawleRequestAsync(str, isLinkFromRoot, cancellationToken);
                    }
                    innerLinks = null;
                    
                    await sql.PageInsertOrUpdateOk(page, cancellationToken);
                }

                page = null;
                return true;
            }
            catch (TaskCanceledException) { return false; }
            catch (Exception ex)
            {
                Trace.TraceError("CrawlerManager.CrawleOneAsync Exception for " + url + " : " + ex.GetBaseException().ToString());
#if DEBUG
                if (Debugger.IsAttached && !ex.Message.Contains("Timeout") & !ex.Message.Contains("A transport-level error has occurred")) { Debugger.Break(); }
#endif
                return false;
            }
        }
        
    }
}
