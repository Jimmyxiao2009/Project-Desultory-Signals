"""Read redacted diagnostics and run the fixed on-device read/playback checks."""
import argparse, base64, json, re, ssl, time, urllib.request, urllib.parse, urllib.error
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("action", choices=["status", "collect", "start-check", "cold-launch", "reopen", "suspend", "deploy", "start-login", "collect-login"])
parser.add_argument("--device", default="192.168.3.159")
parser.add_argument("--appx", type=Path)
args = parser.parse_args()
base = "https://" + args.device
ctx = ssl._create_unverified_context()
csrf = None

def request(path, method="GET", body=None, content_type=None):
    headers = {"X-CSRF-Token": csrf} if csrf else {}
    if content_type: headers["Content-Type"] = content_type
    req = urllib.request.Request(base + path, data=body, headers=headers, method=method)
    with urllib.request.urlopen(req, context=ctx, timeout=90) as res:
        return res.status, res.read(), res.headers

_, _, headers = request("/api/os/info")
csrf = re.search(r"CSRF-Token=([^;,\s]+)", headers.get("Set-Cookie", "")).group(1)
_, body, _ = request("/api/app/packagemanager/packages")
packages = [p for p in json.loads(body)["InstalledPackages"] if p["PackageFullName"].startswith("DesultorySignals.DebugShell_")]
if len(packages) != 1: raise RuntimeError("Expected exactly one installed debug shell")
package = packages[0]
full = package["PackageFullName"]
b64 = lambda s: urllib.parse.quote(base64.b64encode(s.encode()).decode(), safe="")
control = "/api/taskmanager/app?package=" + b64(full)
folder = "/api/filesystem/apps/" + "file?" + urllib.parse.urlencode({"knownfolderid": "LocalAppData", "packagefullname": full, "path": "\\LocalState"})

def status():
    _, body, _ = request("/api/resourcemanager/processes")
    processes = [p for p in json.loads(body)["Processes"] if p.get("PackageFullName") == full]
    print(json.dumps({"package": full, "processes": processes}, ensure_ascii=False), flush=True)
    return processes

def launch():
    code, _, _ = request(control + "&appid=" + b64(package["PackageRelativeId"]), "POST")
    print("launch HTTP", code, flush=True)

if args.action == "status": status()
elif args.action in ("collect", "collect-login"):
    out = Path(__file__).resolve().parent.parent / "crash"
    out.mkdir(exist_ok=True)
    names = ["login-check-report.json"] if args.action == "collect-login" else ["startup-report.txt", "runtime-log.txt", "device-check-report.json", "login-check-report.json"]
    for name in names:
        try:
            _, body, _ = request(folder + "&filename=" + name)
            target = out / ("device-" + name)
            target.write_bytes(body)
            if name.endswith(".json"):
                doc = json.loads(body)
                if doc.get("package") and doc["package"] != full:
                    print("stale report from", doc["package"], "saved; current package is", full, flush=True)
                else:
                    print(json.dumps(doc, ensure_ascii=False), flush=True)
            elif name == "startup-report.txt":
                doc = json.loads(body)
                print(json.dumps({k: doc.get(k) for k in ["writtenAtUtc", "healthy", "runtimeDescribe", "sessionState", "feedProbe", "feedProbeError"]}, ensure_ascii=False), flush=True)
            else: print("log saved:", target, "bytes:", len(body), flush=True)
        except urllib.error.HTTPError as e:
            if e.code != 404: raise
            print(name, "not yet written", flush=True)
elif args.action == "deploy":
    if not args.appx or not args.appx.is_file(): raise RuntimeError("Pass --appx with the built application package")
    import zipfile, xml.etree.ElementTree as ET
    with zipfile.ZipFile(args.appx) as archive:
        root = ET.fromstring(archive.read("AppxManifest.xml"))
        item = root.find("{*}Identity")
        if item.attrib["Name"] != "DesultorySignals.DebugShell" or item.attrib["ProcessorArchitecture"] != "arm":
            raise RuntimeError("Only the ARM32 debug shell may be updated by this tool")
        expected = "DesultorySignals.DebugShell_" + item.attrib["Version"] + "_arm_"
    if status():
        code, _, _ = request(control + "&forcestop=true", "DELETE")
        print("stop HTTP", code, flush=True)
        time.sleep(2)
    boundary = "DSUpdate" + str(int(time.time()))
    name = args.appx.name
    body = ("--" + boundary + '\r\nContent-Disposition: form-data; name="' + name + '"; filename="' + name + '"\r\nContent-Type: application/octet-stream\r\n\r\n').encode() + args.appx.read_bytes() + ('\r\n--' + boundary + '--\r\n').encode()
    code, _, _ = request("/api/app/packagemanager/package?package=" + urllib.parse.quote(name), "POST", body, "multipart/form-data; boundary=" + boundary)
    print("upload HTTP", code, flush=True)
    deadline = time.monotonic() + 240
    while time.monotonic() < deadline:
        time.sleep(2)
        code, result, _ = request("/api/app/packagemanager/state")
        if code == 204 or not result: continue
        state = json.loads(result)
        if state.get("Success") is True: break
        if state.get("Code") and "InProgress" not in state.get("CodeText", ""): raise RuntimeError(state)
    else: raise RuntimeError("Deployment timed out")
    _, result, _ = request("/api/app/packagemanager/packages")
    if not any(p["PackageFullName"].startswith(expected) for p in json.loads(result)["InstalledPackages"]):
        raise RuntimeError("Expected package version was not registered")
    print("installed", expected, flush=True)
elif args.action == "suspend":
    code, _, _ = request("/api/taskmanager/app/state?package=" + b64(full) + "&state=suspend", "POST")
    print("suspend HTTP", code)
    time.sleep(2)
    status()
else:
    if args.action in ("cold-launch", "start-check", "start-login"):
        if status():
            code, _, _ = request(control + "&forcestop=true", "DELETE")
            print("stop HTTP", code, flush=True)
            time.sleep(2)
    if args.action in ("start-check", "start-login"):
        boundary = "DesultoryDeviceCheck20261007"
        name = "login-check.txt" if args.action == "start-login" else "device-check.txt"
        body = ("--" + boundary + '\r\nContent-Disposition: form-data; name="' + name + '"; filename="' + name + '"\r\nContent-Type: application/octet-stream\r\n\r\nread and playback checks\r\n--' + boundary + '--\r\n').encode()
        code, _, _ = request(folder + "&extract=false", "POST", body, "multipart/form-data; boundary=" + boundary)
        print("check requested HTTP", code, flush=True)
    launch()
    time.sleep(3)
    status()
