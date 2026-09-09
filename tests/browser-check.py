"""Run with Playwright and CLIP_FIXTURE_VIDEO pointing to a >=120-second H.264 MP4.
Uses the actual injected JS/CSS, a local API fixture, and real browser video playback.
"""
import json, os, threading, uuid
from pathlib import Path
from urllib.parse import urlsplit, parse_qs
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from playwright.sync_api import sync_playwright

repo = Path(__file__).resolve().parents[1]
assets = repo / 'Jellyfin.Plugin.Screenshot/js'
video = Path(os.environ['CLIP_FIXTURE_VIDEO']).read_bytes()
output = Path(os.environ.get('CLIP_BROWSER_OUTPUT', '/tmp/clip-browser-checks'))
output.mkdir(parents=True, exist_ok=True)
requests = []
unauthorized = []
authenticated = []
settings = {'anchor':70,'runtime':200,'fail':False,'delay':False}
item = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def send(self, data, mime='application/json', code=200):
        if not isinstance(data, bytes): data = data.encode()
        self.send_response(code); self.send_header('Content-Type', mime); self.send_header('Content-Length',len(data)); self.end_headers()
        try: self.wfile.write(data)
        except (BrokenPipeError, ConnectionResetError): pass
    def authorize(self, media=False):
        query = parse_qs(urlsplit(self.path).query)
        header = self.headers.get('Authorization', '')
        valid = (query.get('ApiKey') == ['fixture-token'] if media else
                 header.startswith('MediaBrowser ') and 'Token="fixture-token"' in header
                 and 'DeviceId="fixture-device"' in header)
        # Model a server with legacy authorization disabled, including media range requests.
        valid = valid and 'api_key' not in query and not self.headers.get('X-Emby-Token')
        if not valid:
            unauthorized.append((self.command, urlsplit(self.path).path))
            self.send('Unauthorized', 'text/plain', 401)
            return False
        authenticated.append((self.command, urlsplit(self.path).path))
        return True
    def do_GET(self):
        path = self.path.split('?')[0]
        if path == '/material-icons.woff2' and (output/'material-icons.woff2').exists():
            self.send((output/'material-icons.woff2').read_bytes(), 'font/woff2')
        elif path == '/fixture':
            self.send(f"""<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><style>@font-face{{font-family:Material Icons;src:url(/material-icons.woff2)}}body{{background:#10161c;color:white;font:14px Arial;margin:20px}}.videoOsdBottom{{position:fixed;bottom:25px;right:30px}}button{{padding:8px}}.material-icons{{font-family:Material Icons;font-size:23px}}video#main{{width:100px;height:60px}}</style></head><body>
            <h2>Jellyfin player fixture</h2><video id="main"></video><div class="videoOsdBottom"><input class="osdPositionSlider" value="35"><div class="buttons focuscontainer-x"><button class="btnUserRating" data-id="{item}"></button><button class="btnVideoOsdSettings">Settings</button></div></div>
            <script>location.hash='/video';window.fixtureAnchor={settings['anchor']};window.ApiClient={{serverAddress:()=>location.origin+'/jellyfin',accessToken:()=>'fixture-token',deviceId:()=>'fixture-device'}};
            window.require=(modules,callback)=>callback({{getCurrentPlayer:()=>({{}}),currentItem:()=>({{Id:'{item}'}}),getCurrentTicks:()=>window.fixtureAnchor*10000000}});</script>
            <script src="/jellyfin/Screenshot/script"></script></body></html>""", 'text/html')
        elif path == '/jellyfin/Screenshot/script': self.send((assets/'screenshot.js').read_text()+'\n;\n'+(assets/'clipping.js').read_text(), 'application/javascript')
        elif path == '/jellyfin/Screenshot/clipping.css': self.send((assets/'clipping.css').read_bytes(), 'text/css')
        elif path == '/jellyfin/Sessions':
            if not self.authorize(): return
            assert parse_qs(urlsplit(self.path).query).get('DeviceId') == ['fixture-device']
            self.send(json.dumps([{'DeviceId':'fixture-device','NowPlayingItem':{'Id':item,'Name':'Aspect ratio test','RunTimeTicks':settings['runtime']*10000000},'PlayState':{'PositionTicks':settings['anchor']*10000000,'MediaSourceId':'selected-version','AudioStreamIndex':1,'SubtitleStreamIndex':2}}]))
        elif path.startswith('/jellyfin/Screenshot/clips/'):
            if not self.authorize(media=True): return
            # Range requests exercise the video element just like FileStreamResult.
            start,end=0,len(video)-1
            if self.headers.get('Range'):
                parts=self.headers['Range'].removeprefix('bytes=').split('-');start=int(parts[0]);end=min(int(parts[1]) if parts[1] else end,end)
                self.send_response(206);self.send_header('Content-Range',f'bytes {start}-{end}/{len(video)}')
            else: self.send_response(200)
            self.send_header('Content-Type','video/mp4');self.send_header('Accept-Ranges','bytes');self.send_header('Content-Length',end-start+1)
            if 'download=true' in self.path:self.send_header('Content-Disposition','attachment; filename="test-clip.mp4"')
            self.end_headers()
            try:self.wfile.write(video[start:end+1])
            except (BrokenPipeError,ConnectionResetError):pass
        else:self.send('not found','text/plain',404)
    def do_POST(self):
        body=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        requests.append(body)
        if not self.authorize(): return
        if settings['fail']:
            self.send('Rendering failed for test.','text/plain',500);return
        if settings['delay']:
            import time;time.sleep(1)
        anchor=body['AnchorTicks'];start=max(0,anchor-body['BeforeTicks']);end=min(settings['runtime']*10000000,anchor+body['AfterTicks'])
        self.send(json.dumps({'Id':str(uuid.uuid4()),'Filename':'test-clip.mp4','StartTicks':start,'EndTicks':end}))
    def do_DELETE(self):
        if self.authorize(): self.send('',code=204)

server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
threading.Thread(target=server.serve_forever,daemon=True).start()
url=f'http://127.0.0.1:{server.server_port}/fixture'
with sync_playwright() as p:
    browser=p.chromium.launch(headless=True,args=['--no-sandbox','--disable-dev-shm-usage'])
    page=browser.new_page(viewport={'width':1440,'height':1050},accept_downloads=True)
    errors=[];page.on('pageerror',lambda err:errors.append(str(err)))
    page.goto(url)
    page.locator('#screenshot-clip-btn').click()
    page.wait_for_function('document.querySelector(".jfc-export")?.disabled===false')
    assert requests[-1]['Preview'] and requests[-1]['AnchorTicks']==700000000
    assert requests[-1]['MediaSourceId']=='selected-version' and requests[-1]['AudioStreamIndex']==1
    box=page.locator('.jfc-preview').bounding_box();assert abs(box['width']/box['height']-16/9)<.01
    assert page.locator('[data-clip-preview]').evaluate('el=>getComputedStyle(el).objectFit')=='contain'
    page.wait_for_function('document.querySelectorAll(".jfc-frames img").length===8')
    page.screenshot(path=str(output/'focus-desktop.png'),full_page=True)
    page.locator('[data-jfc-preset="60,60"]').click()
    assert page.locator('#jfc-before').input_value()=='60'
    assert page.locator('#jfc-after').input_value()=='60'
    page.locator('#jfc-before').fill('999');assert page.locator('#jfc-before').input_value()=='60'
    page.locator('#jfc-before').fill('0');page.locator('#jfc-after').fill('0');assert page.locator('.jfc-export').is_disabled()
    page.locator('[data-jfc-preset="15,15"]').click()
    page.locator('.jfc-start').focus();page.keyboard.press('ArrowLeft');assert page.locator('#jfc-before').input_value()=='16'
    track=page.locator('.jfc-timeline').bounding_box();handle=page.locator('.jfc-start').bounding_box()
    page.mouse.move(handle['x']+7,handle['y']+20);page.mouse.down();page.mouse.move(track['x'],track['y']+20,steps=8);page.mouse.up()
    assert page.locator('#jfc-before').input_value()=='60'
    page.locator('[data-jfc-preset="15,15"]').click();page.locator('.jfc-play').click()
    page.wait_for_timeout(1100);assert page.locator('[data-clip-preview]').evaluate('el=>el.currentTime')>45
    page.locator('.jfc-play').click()
    page.locator('#jfc-subtitles').check();page.wait_for_function('document.querySelector(".jfc-export")?.disabled===false')
    assert requests[-1]['SubtitleStreamIndex']==2
    with page.expect_download() as download:page.locator('.jfc-export').click()
    assert download.value.suggested_filename=='test-clip.mp4'
    assert not requests[-1]['Preview'] and requests[-1]['BeforeTicks']==150000000 and requests[-1]['AfterTicks']==150000000
    assert requests[-1]['AnchorTicks']==700000000
    page.locator('.jfc-close').click();assert page.locator('#jfclip-editor').count()==0
    assert page.locator('#screenshot-clip-btn').evaluate('el=>el===document.activeElement')
    # Native download receives only a prepared URL, preserving the CEF navigation state.
    page.evaluate('window.jmpNative={startDownload:url=>window.nativeDownload=url}')
    page.locator('#screenshot-clip-btn').click();page.wait_for_function('document.querySelector(".jfc-export")?.disabled===false');page.locator('.jfc-export').click()
    page.wait_for_function('window.nativeDownload');assert 'download=true' in page.evaluate('window.nativeDownload')
    assert page.request.get(page.evaluate('window.nativeDownload')).status == 200
    page.keyboard.press('Escape')
    # Error/retry and closing during preparation must leave no orphan dialog.
    settings['fail']=True
    page.locator('#screenshot-clip-btn').click();page.locator('.jfc-retry').wait_for(state='visible')
    assert page.locator('.jfc-export').is_disabled()
    settings['fail']=False;page.locator('.jfc-retry').click();page.wait_for_function('document.querySelector(".jfc-export")?.disabled===false');page.keyboard.press('Escape')
    settings['delay']=True;page.locator('#screenshot-clip-btn').click();page.locator('#jfclip-editor').wait_for();page.keyboard.press('Escape');page.wait_for_timeout(1200);assert page.locator('#jfclip-editor').count()==0;settings['delay']=False
    # Beginning/end of media and a narrow viewport.
    for anchor,runtime in [(5,10),(198,200)]:
        settings.update(anchor=anchor,runtime=runtime)
        page.set_viewport_size({'width':390,'height':844});page.goto(url)
        page.locator('#screenshot-clip-btn').click();page.wait_for_function('document.querySelector(".jfc-export")?.disabled===false')
        page.locator('[data-jfc-preset="60,60"]').click()
        assert float(page.locator('#jfc-before').input_value())==min(60,anchor)
        assert float(page.locator('#jfc-after').input_value())==min(60,runtime-anchor)
        box=page.locator('.jfc-preview').bounding_box();assert abs(box['width']/box['height']-16/9)<.01
        assert page.evaluate('document.documentElement.scrollWidth<=innerWidth')
        page.screenshot(path=str(output/f'focus-mobile-{anchor}.png'),full_page=True)
        page.keyboard.press('Escape')
    assert not errors,errors
    assert not unauthorized,unauthorized
    assert any(method == 'DELETE' for method, _ in authenticated)
    (output/'browser-checks.json').write_text(json.dumps({'status':'passed','requests':len(requests),'errors':errors,'checks':['modern authorization with legacy auth disabled','button injection','real preview playback and seeking','real thumbnails','16:9 contain','bounds and zero length','drag and keyboard trimming','fixed anchor','selected source/audio/subtitles','browser download','native download','retry','close during prepare','media boundaries','mobile overflow']},indent=2))
    browser.close()
server.shutdown()
print('PASS: production editor, real video playback/thumbnails, trimming, downloads, cancellation, retries, and mobile boundaries')
