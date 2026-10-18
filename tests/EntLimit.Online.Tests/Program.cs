using EntLimit.Online;
using System.Net;
using System.Text;
using System.Text.Json;
int count=0;
void Check(bool value,string name) { if (!value) throw new Exception(name); count++; }
void Reject(string endpoint) { try { ApiEndpoint.Validate(endpoint); throw new Exception("accepted unsafe endpoint"); } catch (InvalidOperationException) { count++; } }
Reject("http://example.com/v1"); Reject("https://u:p@example.com/v1"); Reject("https://example.com/v1?key=secret"); Reject("https://example.com/#secret"); Reject("file:///C:/x");
Check(ApiEndpoint.Completion("https://example.com/v1").AbsolutePath=="/v1/chat/completions","base endpoint");
Check(ApiEndpoint.Completion("http://localhost:1234/v1/chat/completions").Port==1234,"local endpoint");
Check(ApiEndpoint.Validate("http://127.0.0.1:1234/v1").IsLoopback,"literal loopback");
using(var client=new CompatibleApiClient(new FakeHandler(async request=> { string sent=await request.Content!.ReadAsStringAsync(); Check(request.Headers.Authorization?.Parameter=="fake-key-only","header"); Check(!sent.Contains("fake-key-only"),"no key in prompt"); return new(HttpStatusCode.OK) { Content=new StringContent("{\"choices\":[{\"message\":{\"content\":\"hello fake-key-only\"}}]}") }; })))
 Check(await client.CompleteAsync(new(){Endpoint="https://example.invalid/v1",Model="test-model"}," fake-key-only ","system","user")=="hello [已隐藏密钥]","redact echoed key");
using(var client=new CompatibleApiClient(new FakeHandler(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content=new StringContent("fake-key-only provider private response") }))))
 try { await client.CompleteAsync(new(){Endpoint="https://example.invalid",Model="test"},"fake-key-only","s","u"); throw new Exception("expected http failure"); } catch(InvalidOperationException e) { Check(!e.ToString().Contains("fake-key-only")&&!e.ToString().Contains("private response"),"safe error"); }
using(var client=new CompatibleApiClient(new FakeHandler(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(new string('x',CompatibleApiClient.MaxResponseBytes+1)) }))))
 try { await client.CompleteAsync(new(){Endpoint="https://example.invalid",Model="test"},"","s","u"); throw new Exception("size"); } catch(InvalidOperationException) { count++; }
using(var client=new CompatibleApiClient(new CancelHandler()))
{
 using var stop=new CancellationTokenSource(25);
 try { await client.CompleteAsync(new(){Endpoint="https://example.invalid",Model="test"},"fake-key","s","u",stop.Token); throw new Exception("cancel expected"); } catch(OperationCanceledException) { count++; }
}
foreach(string invalid in new[]{"{}","{\"choices\":[]}","{\"choices\":[{\"message\":{\"content\":{\"secret\":\"fake-key\"}}}]}","{not json fake-key"})
{
 using var client=new CompatibleApiClient(new FakeHandler(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(invalid)})));
 try { await client.CompleteAsync(new(){Endpoint="https://example.invalid",Model="test"},"fake-key","s","u"); throw new Exception("bad protocol"); } catch(InvalidOperationException ex) { Check(!ex.ToString().Contains("fake-key"),"safe malformed reply"); }
}
using(var client=new CompatibleApiClient(new FakeHandler(_=>throw new Exception("must not send HTTP key"))))
 try { await client.CompleteAsync(new(){Endpoint="http://localhost:8080/v1",Model="test"},"fake-key","s","u"); throw new Exception("http key"); } catch(InvalidOperationException) {count++;}
using(var client=new CompatibleApiClient(new FakeHandler(_=>throw new Exception("must not send unicode key"))))
 try { await client.CompleteAsync(new(){Endpoint="https://example.invalid",Model="test"},"假密钥","s","u"); throw new Exception("unicode key"); } catch(InvalidOperationException) {count++;}
if(OperatingSystem.IsWindows())
{
 string root=Path.Combine(Path.GetTempPath(),"EntLimit-Online-Test-"+Guid.NewGuid().ToString("N"));
 try { var store=new ProtectedApiStore(root); store.SaveSettings(new(){Endpoint="https://example.invalid/v1",Model="test"}); store.SaveKey("https://example.invalid/v1","fake-secret-never-production"); Check(store.ReadKey("https://example.invalid/v1")=="fake-secret-never-production","dpapi roundtrip"); Check(!Encoding.UTF8.GetString(File.ReadAllBytes(store.SecretPath)).Contains("fake-secret-never-production"),"no plaintext cipherfile"); Check(!File.ReadAllText(store.SettingsPath).Contains("fake-secret"),"no plaintext settings"); try { store.ReadKey("https://other.invalid/v1"); throw new Exception("endpoint mismatch"); } catch(InvalidOperationException) { count++; } store.DeleteKey(); Check(!store.HasKey,"delete"); }
 finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
}
Console.WriteLine($"PASS {count} Online checks (fake handler and fake key only).");
sealed class FakeHandler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request); }

sealed class CancelHandler:HttpMessageHandler
{
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) { await Task.Delay(Timeout.Infinite,token); return new(HttpStatusCode.OK); }
}
