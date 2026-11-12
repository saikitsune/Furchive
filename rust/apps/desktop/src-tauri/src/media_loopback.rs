//! Loopback transport contract: fixed qualification media only, never a filesystem server.
use std::io::{Read, Write};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
};
use std::thread::JoinHandle;
use std::time::Duration;

/// Only embedded, harmless fixture bytes are reachable. No user paths, remote proxy or IPC.
/// A new 256-bit capability and ephemeral IPv4 loopback port are generated each launch.
pub struct MediaServer {
    address: SocketAddr,
    prefix: String,
    stop: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
}
impl MediaServer {
    pub fn start() -> std::io::Result<Self> {
        let listener = TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, 0))?;
        listener.set_nonblocking(true)?;
        let address = listener.local_addr()?;
        let mut random = [0u8; 32];
        getrandom::fill(&mut random).map_err(std::io::Error::other)?;
        let token = random
            .iter()
            .map(|b| format!("{b:02x}"))
            .collect::<String>();
        let prefix = format!("/{token}/");
        let stop = Arc::new(AtomicBool::new(false));
        let signal = stop.clone();
        let route = prefix.clone();
        let worker = std::thread::Builder::new()
            .name("furchive-fixture-media".into())
            .spawn(move || {
                while !signal.load(Ordering::Acquire) {
                    match listener.accept() {
                        Ok((mut stream, _)) => {
                            let result = serve(&mut stream, address, &route);
                            #[cfg(feature = "self-test")]
                            if let Err(error) = &result {
                                eprintln!("fixture transport I/O: {:?}", error.kind());
                            }
                            let _ = result;
                        }
                        Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                            std::thread::sleep(Duration::from_millis(10))
                        }
                        Err(_) => break,
                    }
                }
            })?;
        Ok(Self {
            address,
            prefix,
            stop,
            worker: Some(worker),
        })
    }
    pub fn address(&self) -> SocketAddr {
        self.address
    }
    pub fn base_url(&self) -> String {
        format!("http://{}{}", self.address, self.prefix)
    }
}
impl Drop for MediaServer {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
fn serve(stream: &mut TcpStream, address: SocketAddr, prefix: &str) -> std::io::Result<()> {
    // Accepted sockets may inherit O_NONBLOCK on BSD/macOS; deadlines alone do
    // not change that flag. Normalize this owned connection before bounded I/O.
    stream.set_nonblocking(false)?;
    stream.set_read_timeout(Some(Duration::from_millis(750)))?;
    stream.set_write_timeout(Some(Duration::from_millis(750)))?;
    let mut raw = Vec::new();
    let deadline = std::time::Instant::now() + Duration::from_millis(1500);
    loop {
        if std::time::Instant::now() >= deadline {
            return empty(stream, 408);
        }
        let mut buf = [0u8; 1024];
        let n = stream.read(&mut buf)?;
        if n == 0 {
            return Ok(());
        }
        raw.extend_from_slice(&buf[..n]);
        if raw.len() > 8192 {
            return empty(stream, 431);
        }
        if raw.windows(4).any(|x| x == b"\r\n\r\n") {
            break;
        }
    }
    let text = match std::str::from_utf8(&raw) {
        Ok(v) => v,
        Err(_) => return empty(stream, 400),
    };
    let mut lines = text.split("\r\n");
    let parts = lines
        .next()
        .unwrap_or("")
        .split_whitespace()
        .collect::<Vec<_>>();
    if parts.len() != 3 || parts[2] != "HTTP/1.1" {
        return empty(stream, 400);
    }
    let mut headers = std::collections::HashMap::new();
    for line in lines.take_while(|s| !s.is_empty()) {
        let Some((key, value)) = line.split_once(':') else {
            return empty(stream, 400);
        };
        if headers
            .insert(key.to_ascii_lowercase(), value.trim())
            .is_some()
        {
            #[cfg(feature = "self-test")]
            eprintln!("fixture duplicate header refused");
            return empty(stream, 400);
        }
    }
    #[cfg(feature = "self-test")]
    eprintln!(
        "fixture request: verb={} version={} header_count={} range={:?} origin={:?}",
        parts[0],
        parts[2],
        headers.len(),
        headers.get("range"),
        headers.get("origin")
    );
    if headers.get("host").copied() != Some(address.to_string().as_str()) {
        return empty(stream, 403);
    }
    // Reject browser requests from every origin except the bundled app (or a no-Origin media GET).
    if headers.get("origin").is_some_and(|v| {
        !matches!(
            *v,
            "tauri://localhost" | "http://tauri.localhost" | "https://tauri.localhost"
        )
    }) {
        return empty(stream, 403);
    }
    if !matches!(parts[0], "GET" | "HEAD") {
        return empty(stream, 405);
    }
    let name = parts[1].strip_prefix(prefix).unwrap_or("");
    let (data, mime): (&[u8], &str) = match name {
        "vp8.webm" => (
            include_bytes!("../../public/fixtures/vp8.webm"),
            "video/webm",
        ),
        "vp9.webm" => (
            include_bytes!("../../public/fixtures/vp9.webm"),
            "video/webm",
        ),
        "h264.mp4" => (
            include_bytes!("../../public/fixtures/h264.mp4"),
            "video/mp4",
        ),
        _ => return empty(stream, 404),
    };
    match crate::media_protocol::select_range(headers.get("range").copied(), data.len()) {
        Ok((status, start, end)) => {
            #[cfg(feature = "self-test")]
            eprintln!(
                "fixture response: verb={} status={status} span={start}-{end} total={}",
                parts[0],
                data.len()
            );
            write!(
                stream,
                "HTTP/1.1 {status} OK\r\nContent-Type: {mime}\r\nContent-Length: {}\r\nAccept-Ranges: bytes\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n",
                end - start
            )?;
            if status == 206 {
                write!(
                    stream,
                    "Content-Range: bytes {}-{}/{}\r\n",
                    start,
                    end - 1,
                    data.len()
                )?;
            }
            stream.write_all(b"\r\n")?;
            if parts[0] == "GET" {
                stream.write_all(&data[start..end])?;
            }
            Ok(())
        }
        Err(()) => write!(
            stream,
            "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            data.len()
        ),
    }
}
fn empty(stream: &mut TcpStream, status: u16) -> std::io::Result<()> {
    #[cfg(feature = "self-test")]
    eprintln!("fixture response refused: status={status}");
    write!(
        stream,
        "HTTP/1.1 {status} Refused\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Read, Write};
    use std::net::TcpStream;

    fn request(server: &MediaServer, path: &str, headers: &str, method: &str) -> Vec<u8> {
        let mut stream = TcpStream::connect(server.address()).unwrap();
        stream
            .set_read_timeout(Some(std::time::Duration::from_secs(3)))
            .unwrap();
        write!(
            stream,
            "{method} {path} HTTP/1.1\r\nHost: {}\r\n{headers}\r\n",
            server.address()
        )
        .unwrap();
        let mut bytes = Vec::new();
        stream.read_to_end(&mut bytes).unwrap();
        bytes
    }
    #[test]
    fn accepted_nonblocking_socket_is_normalized_before_reading_split_headers() {
        let listener = TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, 0)).unwrap();
        let addr = listener.local_addr().unwrap();
        let peer = std::thread::spawn(move || {
            let mut s = TcpStream::connect(addr).unwrap();
            write!(s, "HEAD /cap/vp8.webm HTTP/1.1\r\nHost: {addr}\r\n").unwrap();
            std::thread::sleep(Duration::from_millis(40));
            s.write_all(b"\r\n").unwrap();
            let mut bytes = Vec::new();
            s.read_to_end(&mut bytes).unwrap();
            bytes
        });
        let (mut socket, _) = listener.accept().unwrap();
        socket.set_nonblocking(true).unwrap();
        assert!(serve(&mut socket, addr, "/cap/").is_ok());
        drop(socket);
        assert!(peer.join().unwrap().starts_with(b"HTTP/1.1 200"));
    }
    #[test]
    fn fixed_media_streams_exact_ranges_and_head_without_filesystem_scope() {
        let server = MediaServer::start().unwrap();
        let path = server
            .base_url()
            .split_once(&server.address().to_string())
            .unwrap()
            .1
            .to_string()
            + "vp8.webm";
        let reply = request(&server, &path, "Range: bytes=2-5\r\n", "GET");
        assert!(reply.starts_with(b"HTTP/1.1 206"));
        let split = reply.windows(4).position(|v| v == b"\r\n\r\n").unwrap() + 4;
        assert_eq!(
            &reply[split..],
            &include_bytes!("../../public/fixtures/vp8.webm")[2..6]
        );
        let head = request(&server, &path, "", "HEAD");
        assert!(head.starts_with(b"HTTP/1.1 200"));
        assert!(head.ends_with(b"\r\n\r\n"));
        assert!(
            request(&server, &(path.clone() + "/../../secret"), "", "GET")
                .starts_with(b"HTTP/1.1 404")
        );
        assert!(request(&server, "/vp8.webm", "", "GET").starts_with(b"HTTP/1.1 404"));
        assert!(
            request(&server, &path, "Origin: https://evil.invalid\r\n", "GET")
                .starts_with(b"HTTP/1.1 403")
        );
        assert!(request(&server, &path, "", "POST").starts_with(b"HTTP/1.1 405"));
    }
}
