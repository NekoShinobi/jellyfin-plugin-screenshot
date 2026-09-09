"""Run with Playwright and CLIP_FIXTURE_VIDEO pointing to a >=120-second H.264 MP4.
CLIP_FIXTURE_WEBM points to its VP8/Opus equivalent (defaults to the same path with .webm).
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
webm_video = Path(os.environ.get('CLIP_FIXTURE_WEBM', str(Path(os.environ['CLIP_FIXTURE_VIDEO']).with_suffix('.webm')))).read_bytes()
clip_formats = {}
output = Path(os.environ.get('CLIP_BROWSER_OUTPUT', '/tmp/clip-browser-checks'))
output.mkdir(parents=True, exist_ok=True)
requests = []
unauthorized = []
authenticated = []
render_gate = threading.Event(); render_gate.set()
media_gate = threading.Event(); media_gate.set()
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
        elif path == '/jellyfin/Screenshot/capture':
            if self.authorize(media=True): self.send(b'fixture-jpeg', 'image/jpeg')
        elif path == '/jellyfin/Screenshot/script': self.send((assets/'screenshot.js').read_text()+'\n;\n'+(assets/'clipping.js').read_text(), 'application/javascript')
        elif path == '/jellyfin/Screenshot/clipping.css': self.send((assets/'clipping.css').read_bytes(), 'text/css')
        elif path == '/jellyfin/Sessions':
            if not self.authorize(): return
            assert parse_qs(urlsplit(self.path).query).get('DeviceId') == ['fixture-device']
            self.send(json.dumps([{'DeviceId':'fixture-device','NowPlayingItem':{'Id':item,'Name':'Aspect ratio test','RunTimeTicks':settings['runtime']*10000000},'PlayState':{'PositionTicks':settings['anchor']*10000000,'MediaSourceId':'selected-version','AudioStreamIndex':1,'SubtitleStreamIndex':2}}]))
        elif path.startswith('/jellyfin/Screenshot/clips/'):
            if not self.authorize(media=True): return
            media_gate.wait(30)
            if settings.get('bad_mp4') and clip_formats.get(path.rsplit('/',1)[-1]) == 'mp4' and 'download=true' not in self.path:
                settings['bad_mp4'] = False
                self.send(b'unsupported mp4 fixture', 'video/mp4'); return
            media = webm_video if clip_formats.get(path.rsplit('/',1)[-1]) == 'webm' else video
            # Range requests exercise the video element just like FileStreamResult.
            start,end=0,len(media)-1
            if self.headers.get('Range'):
                parts=self.headers['Range'].removeprefix('bytes=').split('-');start=int(parts[0]);end=min(int(parts[1]) if parts[1] else end,end)
                self.send_response(206);self.send_header('Content-Range',f'bytes {start}-{end}/{len(media)}')
            else: self.send_response(200)
            self.send_header('Content-Type','video/webm' if media is webm_video else 'video/mp4');self.send_header('Accept-Ranges','bytes');self.send_header('Content-Length',end-start+1)
            if 'download=true' in self.path:self.send_header('Content-Disposition','attachment; filename="test-clip.mp4"')
            self.end_headers()
            try:self.wfile.write(media[start:end+1])
            except (BrokenPipeError,ConnectionResetError):pass
        else:self.send('not found','text/plain',404)
    def do_POST(self):
        body=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        requests.append(body)
        if not self.authorize(): return
        render_gate.wait(30)
        if settings['fail']:
            self.send('Rendering failed for test.','text/plain',500);return
        if settings['delay']:
            import time;time.sleep(1)
        anchor=body['AnchorTicks'];start=body['StartTicks'];end=body['EndTicks']
        assert max(0,anchor-600000000) <= start < end <= min(settings['runtime']*10000000,anchor+600000000)
        clip_id = str(uuid.uuid4())
        clip_formats[clip_id] = body.get('PreviewFormat', 'mp4') if body['Preview'] else 'mp4'
        self.send(json.dumps({'Id':clip_id,'Filename':'test-clip.'+clip_formats[clip_id],'StartTicks':start,'EndTicks':end}))
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
    # Desktop completion supplies the actual renamed path; starting a download
    # is not proof that a file was saved. Results are correlated per capture.
    page.evaluate('window.jmpNative={startDownloadWithResult:(url,requestId)=>{window.pendingScreenshot={url,requestId}}}')
    def request_screenshot():
        page.evaluate('window.pendingScreenshot=null')
        page.locator('#screenshot-capture-btn').click()
        page.locator('[data-capture-mode="without-subtitles"]').click()
        page.wait_for_function('window.pendingScreenshot')
        assert 'saved to' not in page.locator('#screenshot-capture-toast').inner_text()
    def report_screenshot(status, path=''):
        page.evaluate("([status,fullPath])=>window.dispatchEvent(new CustomEvent('jellyfin-download-result',{detail:{...window.pendingScreenshot,status,fullPath}}))", [status,path])
    request_screenshot()
    page.evaluate("window.dispatchEvent(new CustomEvent('jellyfin-download-result',{detail:{requestId:'unrelated',status:'complete',fullPath:'/wrong.jpg'}}))")
    assert '/wrong.jpg' not in page.locator('#screenshot-capture-toast').inner_text()
    saved_path = 'C:/Users/Example/Pictures/映画 & \"renamed\" <frame>.jpg'
    report_screenshot('complete', saved_path)
    page.wait_for_function('document.querySelector("#screenshot-capture-toast")?.textContent.startsWith("Screenshot saved to:")')
    assert page.locator('#screenshot-capture-toast').inner_text() == 'Screenshot saved to:\n'+saved_path
    assert page.locator('#screenshot-capture-toast frame').count()==0
    report_screenshot('cancelled') # Duplicate notifications cannot overwrite success.
    assert saved_path in page.locator('#screenshot-capture-toast').inner_text()
    for outcome, message in [('cancelled','save cancelled'),('failed','could not be saved')]:
        request_screenshot();report_screenshot(outcome)
        page.wait_for_function('(message)=>document.querySelector("#screenshot-capture-toast")?.textContent.includes(message)',arg=message)
    # Older Desktop versions retain downloads without inventing a destination.
    page.evaluate('window.jmpNative={startDownload:url=>window.legacyScreenshot=url}')
    page.locator('#screenshot-capture-btn').click();page.locator('[data-capture-mode="without-subtitles"]').click()
    page.wait_for_function('document.querySelector("#screenshot-capture-toast")?.textContent.includes("does not report the saved path")')
    assert page.evaluate('window.legacyScreenshot').startswith(f'http://127.0.0.1:{server.server_port}/jellyfin/Screenshot/capture?')
    # Standard browsers cannot expose the local download path or completion.
    page.evaluate('delete window.jmpNative')
    page.locator('#screenshot-capture-btn').click()
    with page.expect_download() as screenshot_download:page.locator('[data-capture-mode="without-subtitles"]').click()
    assert screenshot_download.value.suggested_filename.endswith('.jpg')
    page.wait_for_function('document.querySelector("#screenshot-capture-toast")?.textContent.includes("browser’s downloads")')
    assert 'saved to:' not in page.locator('#screenshot-capture-toast').inner_text()
    assert Path(screenshot_download.value.path()).read_bytes() == b'fixture-jpeg'
    # Model a browser denying an automatic download after asynchronous rendering.
    # The ready image must remain downloadable with an actual user click.
    page.evaluate("""() => {
        window.blockAutomaticScreenshot = event => {
            if (event.target.closest?.('a[download]') && !event.isTrusted) event.preventDefault();
        };
        window.hostDownloadClicks = 0;
        window.interceptHostDownload = event => {
            if (event.target.closest?.('a[download]')) { window.hostDownloadClicks++; event.preventDefault(); }
        };
        document.addEventListener('click', window.blockAutomaticScreenshot, true);
        document.addEventListener('click', window.interceptHostDownload);
    }""")
    page.locator('#screenshot-capture-btn').click()
    page.locator('[data-capture-mode="without-subtitles"]').click()
    save_link = page.locator('#screenshot-capture-toast a[download]')
    save_link.wait_for(state='visible')
    assert 'download started' not in page.locator('#screenshot-capture-toast').inner_text()
    captures_before = sum(path == '/jellyfin/Screenshot/capture' for _,path in authenticated)
    with page.expect_download() as manual_screenshot: save_link.click()
    assert manual_screenshot.value.suggested_filename == screenshot_download.value.suggested_filename
    assert Path(manual_screenshot.value.path()).read_bytes() == b'fixture-jpeg'
    assert sum(path == '/jellyfin/Screenshot/capture' for _,path in authenticated) == captures_before
    assert page.evaluate('window.hostDownloadClicks') == 0
    page.evaluate("""() => {
        document.removeEventListener('click', window.blockAutomaticScreenshot, true);
        document.removeEventListener('click', window.interceptHostDownload);
    }""")
    page.locator('#screenshot-clip-btn').click()
    page.wait_for_function('document.querySelector(".jfc-export")?.disabled===false')
    assert requests[-1]['Preview'] and requests[-1]['AnchorTicks']==700000000
    assert requests[-1]['MediaSourceId']=='selected-version' and requests[-1]['AudioStreamIndex']==1
    box=page.locator('.jfc-preview').bounding_box();assert abs(box['width']/box['height']-16/9)<.01
    assert page.locator('[data-clip-preview]').evaluate('el=>getComputedStyle(el).objectFit')=='contain'
    page.wait_for_function('document.querySelectorAll(".jfc-frames img").length===8')
    page.screenshot(path=str(output/'focus-desktop.png'),full_page=True)
    page.locator('[data-jfc-preset="60,60"]').click()
    assert page.locator('#jfc-start').input_value()=='10'
    assert page.locator('#jfc-duration').input_value()=='120'
    page.locator('#jfc-start').fill('999');assert page.locator('#jfc-start').input_value()=='130'
    page.locator('#jfc-duration').fill('0');assert page.locator('.jfc-export').is_disabled()
    page.locator('[data-jfc-preset="15,15"]').click()
    page.locator('.jfc-start').focus();page.keyboard.press('ArrowLeft');assert page.locator('#jfc-start').input_value()=='54'
    track=page.locator('.jfc-timeline').bounding_box();handle=page.locator('.jfc-start').bounding_box()
    page.mouse.move(handle['x']+7,handle['y']+20);page.mouse.down();page.mouse.move(track['x'],track['y']+20,steps=8);page.mouse.up()
    assert page.locator('#jfc-start').input_value()=='10'
    # Reel scrubbing moves the preview across the frozen window without trimming.
    page.locator('[data-jfc-preset="15,15"]').click()
    selection = [page.locator('#jfc-start').input_value(), page.locator('#jfc-duration').input_value()]
    track = page.locator('.jfc-timeline').bounding_box()
    x, y, width = track['x'], track['y'] + track['height']/2, track['width']
    page.locator('.jfc-play').click()
    page.mouse.move(x + width*.2, y);page.mouse.down()
    page.wait_for_function('document.querySelector("[data-clip-preview]").paused')
    page.wait_for_function('Math.abs(document.querySelector("[data-clip-preview]").currentTime - 24) < .2')
    page.mouse.move(x + width*.8, y, steps=8)
    page.wait_for_function('Math.abs(document.querySelector("[data-clip-preview]").currentTime - 96) < .2')
    # Pointer capture keeps dragging active outside the reel and clamps at both ends.
    page.mouse.move(x + width + 40, y)
    page.wait_for_function('Math.abs(document.querySelector("[data-clip-preview]").currentTime - 120) < .2')
    page.mouse.move(x - 40, y)
    page.wait_for_function('document.querySelector("[data-clip-preview]").currentTime < .2')
    page.mouse.up();page.mouse.move(x + width*.9, y)
    assert page.locator('[data-clip-preview]').evaluate('el=>el.currentTime') < .2
    assert not page.locator('.jfc-scrubbing').count()
    # Cancellation releases the gesture, and non-primary clicks do not seek.
    page.mouse.move(x + width*.2, y);page.mouse.down()
    page.locator('.jfc-scrubber').dispatch_event('pointercancel', {'pointerId':1})
    page.mouse.move(x + width*.8, y);page.mouse.up()
    assert abs(page.locator('[data-clip-preview]').evaluate('el=>el.currentTime') - 24) < .2
    page.mouse.click(x + width*.7, y, button='right')
    assert abs(page.locator('[data-clip-preview]').evaluate('el=>el.currentTime') - 24) < .2
    scrubber=page.locator('.jfc-scrubber');scrubber.focus()
    page.keyboard.press('Home');page.keyboard.press('Shift+ArrowRight')
    page.wait_for_function('Math.abs(document.querySelector("[data-clip-preview]").currentTime - 5) < .2')
    assert scrubber.get_attribute('aria-valuenow') == '15'
    assert page.locator('.jfc-preview-time-value').inner_text() == '0:15'
    assert selection == [page.locator('#jfc-start').input_value(), page.locator('#jfc-duration').input_value()]
    page.locator('[data-jfc-preset="15,15"]').click();page.locator('.jfc-play').click()
    page.wait_for_timeout(1100);assert page.locator('[data-clip-preview]').evaluate('el=>el.currentTime')>45
    # The desktop's idle CSS must not hide the editor cursor, and time updates
    # must not replace the pause hit target midway through a real mouse click.
    page.add_style_tag(content='body.mouseIdle, body.mouseIdle * { cursor:none!important; }')
    page.evaluate('''() => {
        document.body.classList.add('mouseIdle');
        window.pauseIcon=document.querySelector('.jfc-play .jfc-icon');
        window.hostClicks=0;
        document.addEventListener('click', () => window.hostClicks++);
    }''')
    assert page.locator('.jfc-preview').evaluate('el=>getComputedStyle(el).cursor') != 'none'
    assert page.locator('.jfc-play').evaluate('el=>getComputedStyle(el).opacity') == '1'
    button=page.locator('.jfc-play').bounding_box()
    page.mouse.move(button['x']+button['width']/2,button['y']+button['height']/2)
    page.mouse.down();page.wait_for_timeout(700)
    assert page.evaluate('window.pauseIcon===document.querySelector(".jfc-play .jfc-icon")')
    page.mouse.up()
    page.wait_for_function('document.querySelector("[data-clip-preview]").paused')
    assert page.evaluate('window.hostClicks') == 0
    # Pause remains reliable over repeated play/pause cycles.
    for _ in range(3):
        page.locator('.jfc-play').click()
        page.wait_for_function('!document.querySelector("[data-clip-preview]").paused')
        page.locator('.jfc-play').click()
        page.wait_for_function('document.querySelector("[data-clip-preview]").paused')
    page.locator('#jfc-subtitles').check();page.wait_for_function('document.querySelector(".jfc-export")?.disabled===false')
    assert requests[-1]['SubtitleStreamIndex']==2
    with page.expect_download() as download:page.locator('.jfc-export').click()
    assert download.value.suggested_filename=='test-clip.mp4'
    assert not requests[-1]['Preview'] and requests[-1]['StartTicks']==550000000 and requests[-1]['EndTicks']==850000000
    assert requests[-1]['AnchorTicks']==700000000
    # Move the end handle before the moment, and export a selection wholly before it.
    page.locator('.jfc-end').focus();page.keyboard.press('Home')
    for _ in range(10): page.keyboard.press('ArrowRight')
    assert page.locator('#jfc-start').input_value() == '55'
    assert page.locator('#jfc-duration').input_value() == '10'
    with page.expect_download(): page.locator('.jfc-export').click()
    assert requests[-1]['StartTicks']==550000000 and requests[-1]['EndTicks']==650000000
    assert requests[-1]['AnchorTicks']==700000000
    # Starting point moves the same duration to the other side of the moment.
    page.locator('#jfc-start').fill('100')
    assert page.locator('#jfc-duration').input_value() == '10'
    with page.expect_download(): page.locator('.jfc-export').click()
    assert requests[-1]['StartTicks']==1000000000 and requests[-1]['EndTicks']==1100000000
    assert requests[-1]['AnchorTicks']==700000000
    # Both handles can cross the marker, but cannot cross each other or the window.
    page.locator('[data-jfc-preset="60,60"]').click()
    page.locator('.jfc-start').focus()
    for _ in range(13): page.keyboard.press('Shift+ArrowRight')
    assert page.locator('#jfc-start').input_value() == '75'
    page.keyboard.press('End');assert page.locator('#jfc-duration').input_value() == '0'
    page.locator('[data-jfc-preset="15,15"]').click()
    page.locator('.jfc-close').click();assert page.locator('#jfclip-editor').count()==0
    assert page.locator('#screenshot-clip-btn').evaluate('el=>el===document.activeElement')
    # Model a CEF build with no MP4 codecs, while playing actual WebM media.
    page.evaluate("""() => {
        window.originalCanPlayType = HTMLMediaElement.prototype.canPlayType;
        window.codecMode = 'webm';
        HTMLMediaElement.prototype.canPlayType = function(type) {
            if (window.codecMode === 'none' || (window.codecMode === 'webm' && type.includes('mp4'))) return '';
            return window.originalCanPlayType.call(this, type);
        };
    }""")
    page.locator('#screenshot-clip-btn').click()
    page.wait_for_function('document.querySelector(".jfc-export")?.disabled===false')
    assert requests[-1]['PreviewFormat'] == 'webm'
    page.wait_for_function('document.querySelectorAll(".jfc-frames img").length===8')
    page.locator('.jfc-play').click();page.wait_for_timeout(300)
    assert not page.locator('[data-clip-preview]').evaluate('el=>el.paused')
    page.locator('.jfc-scrubber').focus();page.keyboard.press('Home');page.keyboard.press('Shift+ArrowRight')
    page.wait_for_function('Math.abs(document.querySelector("[data-clip-preview]").currentTime-5)<.2')
    with page.expect_download() as webm_export: page.locator('.jfc-export').click()
    assert webm_export.value.suggested_filename == 'test-clip.mp4'
    assert not requests[-1]['Preview'] and requests[-1]['PreviewFormat'] == 'mp4'
    page.keyboard.press('Escape')
    # Misreported MP4 support retries once with WebM after a real media error.
    page.evaluate("window.codecMode='default'");settings['bad_mp4']=True
    count=len(requests)
    page.locator('#screenshot-clip-btn').click()
    page.wait_for_function('document.querySelector(".jfc-export")?.disabled===false')
    assert [request['PreviewFormat'] for request in requests[count:]] == ['mp4','webm']
    page.keyboard.press('Escape')
    # Unsupported clients get a specific error before any expensive rendering.
    page.evaluate("window.codecMode='none'");count=len(requests)
    page.locator('#screenshot-clip-btn').click();page.locator('.jfc-retry').wait_for(state='visible')
    assert 'cannot play either supported preview format' in page.locator('.jfc-status').inner_text()
    assert len(requests)==count
    page.keyboard.press('Escape')
    page.evaluate('() => { HTMLMediaElement.prototype.canPlayType=window.originalCanPlayType; }')
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
    # A silent media request must stop the spinner and offer a retry.
    page.clock.install()
    media_gate.clear()
    page.locator('#screenshot-clip-btn').click()
    page.wait_for_function('document.querySelector(".jfc-loading-label")?.textContent === "Loading preview video…"')
    page.clock.fast_forward(31_000)
    page.locator('.jfc-retry').wait_for(state='visible')
    assert 'was created' in page.locator('.jfc-status').inner_text()
    assert page.locator('.jfc-export').is_disabled()
    media_gate.set()
    page.locator('.jfc-retry').click()
    page.wait_for_function('document.querySelector(".jfc-export")?.disabled===false')
    page.keyboard.press('Escape')
    # A render with no response gets a bounded wait and can be retried.
    render_gate.clear()
    page.locator('#screenshot-clip-btn').click()
    page.locator('#jfclip-editor').wait_for()
    page.clock.fast_forward(20_000)
    assert 'server is still rendering' in page.locator('.jfc-status').inner_text()
    page.clock.fast_forward(610_000)
    page.locator('.jfc-retry').wait_for(state='visible')
    assert 'did not finish' in page.locator('.jfc-status').inner_text()
    render_gate.set()
    page.locator('.jfc-retry').click()
    page.wait_for_function('document.querySelector(".jfc-export")?.disabled===false')
    page.keyboard.press('Escape')
    # Beginning/end of media and a narrow viewport.
    for anchor,runtime in [(5,10),(198,200)]:
        settings.update(anchor=anchor,runtime=runtime)
        page.set_viewport_size({'width':390,'height':844});page.goto(url)
        page.locator('#screenshot-clip-btn').click();page.wait_for_function('document.querySelector(".jfc-export")?.disabled===false')
        page.locator('[data-jfc-preset="60,60"]').click()
        assert float(page.locator('#jfc-start').input_value())==max(0,anchor-60)
        assert float(page.locator('#jfc-duration').input_value())==min(60,anchor)+min(60,runtime-anchor)
        box=page.locator('.jfc-preview').bounding_box();assert abs(box['width']/box['height']-16/9)<.01
        assert page.evaluate('document.documentElement.scrollWidth<=innerWidth')
        page.screenshot(path=str(output/f'focus-mobile-{anchor}.png'),full_page=True)
        page.keyboard.press('Escape')
    assert not errors,errors
    assert not unauthorized,unauthorized
    assert any(method == 'DELETE' for method, _ in authenticated)
    (output/'browser-checks.json').write_text(json.dumps({'status':'passed','requests':len(requests),'errors':errors,'checks':['confirmed screenshot path, cancellation, failure and browser fallback','manual screenshot save when automatic downloads are denied','visible idle cursor and reliable pause','selections before and after the fixed moment','stalled render and media timeout with retry','modern authorization with legacy auth disabled','button injection','real preview playback and seeking','real thumbnails','16:9 contain','bounds and zero length','drag and keyboard trimming','reel scrubbing, pointer capture, cancellation and keyboard seeking','fixed anchor','selected source/audio/subtitles','browser download','native download','WebM capability negotiation, real playback and MP4 decode fallback','retry','close during prepare','media boundaries','mobile overflow']},indent=2))
    browser.close()
server.shutdown()
print('PASS: production editor, real video playback/thumbnails, trimming, downloads, cancellation, retries, and mobile boundaries')
