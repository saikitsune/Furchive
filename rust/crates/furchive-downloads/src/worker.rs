use crate::{DuplicatePolicy, Engine, Error, ExpectedHash, Job, Result, State};
use sha2::Digest;
use std::{
    io::{Read, Seek, SeekFrom, Write},
    sync::{atomic::Ordering, Arc},
    time::Duration,
};

fn hashes(file: &mut std::fs::File) -> Result<(String, String)> {
    file.seek(SeekFrom::Start(0))?;
    let mut sha = sha2::Sha256::new();
    let mut md5 = md5::Md5::new();
    let mut buffer = [0; 65536];
    loop {
        let n = file.read(&mut buffer)?;
        if n == 0 {
            break;
        }
        sha.update(&buffer[..n]);
        md5.update(&buffer[..n]);
    }
    Ok((
        format!("{:x}", sha.finalize()),
        format!("{:x}", md5.finalize()),
    ))
}
fn validate_file(file: &mut std::fs::File, job: &Job) -> Result<String> {
    let length = file.metadata()?.len();
    if length != job.bytes
        || job.intent.size.is_some_and(|n| n != length)
        || job.total.is_some_and(|n| n != length)
    {
        return Err(Error::Invalid("size mismatch".into()));
    }
    let (sha, md5) = hashes(file)?;
    if let Some(expected) = &job.intent.hash {
        let matches = match expected {
            ExpectedHash::Sha256(s) => s.eq_ignore_ascii_case(&sha),
            ExpectedHash::Md5(s) => s.eq_ignore_ascii_case(&md5),
        };
        if !matches {
            return Err(Error::Invalid("hash mismatch".into()));
        }
    }
    Ok(sha)
}

pub(crate) fn validator(headers: &reqwest::header::HeaderMap) -> Option<String> {
    if let Some(etag) = headers
        .get(reqwest::header::ETAG)
        .and_then(|v| v.to_str().ok())
    {
        if etag.len() >= 2
            && etag.starts_with('"')
            && etag.ends_with('"')
            && etag.as_bytes()[1..etag.len() - 1]
                .iter()
                .all(|b| *b == 0x21 || (0x23..=0x7e).contains(b) || *b >= 0x80)
        {
            return Some(format!("etag:{etag}"));
        }
    }
    let modified = headers.get(reqwest::header::LAST_MODIFIED)?.to_str().ok()?;
    let modified_time = httpdate::parse_http_date(modified).ok()?;
    let date =
        httpdate::parse_http_date(headers.get(reqwest::header::DATE)?.to_str().ok()?).ok()?;
    // RFC9110: a date validator is strong only with >=60s separation proof.
    (date.duration_since(modified_time).ok()?.as_secs() >= 60).then(|| format!("date:{modified}"))
}
fn parse_range(text: &str) -> Result<(u64, u64, u64)> {
    let parse = || -> Option<(u64, u64, u64)> {
        let (range, total) = text.strip_prefix("bytes ")?.split_once('/')?;
        let (start, end) = range.split_once('-')?;
        let (start, end, total) = (
            start.parse::<u64>().ok()?,
            end.parse::<u64>().ok()?,
            total.parse::<u64>().ok()?,
        );
        (start <= end && end < total).then_some((start, end, total))
    };
    parse().ok_or_else(|| Error::Invalid("invalid Content-Range".into()))
}

#[derive(Debug, Clone, Copy)]
pub enum Control {
    Pause,
    Cancel,
    Resume,
}
struct Command {
    id: i64,
    control: Control,
    reply: tokio::sync::oneshot::Sender<Result<Job>>,
}
pub struct Worker {
    stop: tokio::sync::watch::Sender<bool>,
    join: Option<tokio::task::JoinHandle<Result<()>>>,
    commands: tokio::sync::mpsc::Sender<Command>,
}
impl Worker {
    /// Receipt means durable state committed and any affected writer dropped.
    pub async fn control(&self, id: i64, control: Control) -> Result<Job> {
        let (reply, receipt) = tokio::sync::oneshot::channel();
        self.commands
            .send(Command { id, control, reply })
            .await
            .map_err(|_| Error::Stopped)?;
        receipt.await.map_err(|_| Error::Stopped)?
    }
    pub async fn shutdown(mut self) -> Result<()> {
        let _ = self.stop.send(true);
        self.join
            .take()
            .unwrap()
            .await
            .map_err(|_| Error::Stopped)?
    }
}
impl Drop for Worker {
    fn drop(&mut self) {
        let _ = self.stop.send(true);
    }
}
impl Engine {
    pub fn start(self: &Arc<Self>) -> Result<Worker> {
        self.start_with_concurrency(1)
    }
    pub fn start_with_concurrency(self: &Arc<Self>, limit: usize) -> Result<Worker> {
        if !(1..=4).contains(&limit) {
            return Err(Error::Invalid("concurrency must be 1–4".into()));
        }
        let runtime = tokio::runtime::Handle::try_current()
            .map_err(|_| Error::Invalid("start requires a Tokio runtime".into()))?;
        self.started
            .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
            .map_err(|_| Error::Busy)?;
        let (stop, mut stopped) = tokio::sync::watch::channel(false);
        let (commands, mut received) = tokio::sync::mpsc::channel(64);
        let engine = self.clone();
        let join = runtime.spawn(async move {
            let result = if limit == 1 {
                engine.run(&mut stopped, &mut received).await
            } else {
                engine
                    .run_parallel(&mut stopped, &mut received, limit)
                    .await
            };
            engine.started.store(false, Ordering::SeqCst);
            result
        });
        Ok(Worker {
            stop,
            join: Some(join),
            commands,
        })
    }
    fn apply_control(&self, command: Command) {
        let result = (|| {
            let mut job = self
                .jobs()?
                .into_iter()
                .find(|j| j.id == command.id)
                .ok_or_else(|| Error::Invalid("unknown job".into()))?;
            if matches!(
                job.state,
                State::Queued | State::Running | State::Paused | State::Failed
            ) {
                match command.control {
                    Control::Pause => job.state = State::Paused,
                    Control::Cancel => {
                        job.state = State::Cancelled;
                        self.save(&job)?;
                        self.grant.remove_part(job.id)?;
                    }
                    Control::Resume if matches!(job.state, State::Paused | State::Failed) => {
                        job.state = State::Queued;
                        job.error = None;
                    }
                    Control::Resume => {}
                }
                self.save(&job)?;
            }
            Ok(job)
        })();
        let _ = command.reply.send(result);
    }
    async fn run(
        &self,
        stopped: &mut tokio::sync::watch::Receiver<bool>,
        commands: &mut tokio::sync::mpsc::Receiver<Command>,
    ) -> Result<()> {
        let client = reqwest::Client::builder()
            .use_rustls_tls()
            .no_proxy()
            .redirect(reqwest::redirect::Policy::none())
            .connect_timeout(Duration::from_secs(15))
            .timeout(Duration::from_secs(60))
            .user_agent("Furchive/0.1 (durable-download-core)")
            .build()
            .map_err(|e| Error::Invalid(e.to_string()))?;
        loop {
            if *stopped.borrow() {
                return Ok(());
            }
            while let Ok(command) = commands.try_recv() {
                self.apply_control(command);
            }
            let next = self.jobs()?.into_iter().find(|j| j.state == State::Queued);
            if let Some(mut job) = next {
                job.state = State::Running;
                self.save(&job)?;
                let id = job.id;
                enum Event {
                    Done(Result<()>),
                    Stop,
                    Control(Command),
                }
                let event = {
                    let transfer = self.transfer(&client, &mut job);
                    tokio::pin!(transfer);
                    loop {
                        tokio::select! {
                            biased;
                            _ = stopped.changed() => break Event::Stop,
                            Some(command) = commands.recv() => {
                                if command.id == id && !matches!(command.control, Control::Resume) { break Event::Control(command); }
                                self.apply_control(command);
                            },
                            result = &mut transfer => break Event::Done(result),
                        }
                    }
                };
                let outcome = match event {
                    Event::Done(result) => result,
                    Event::Stop => {
                        job.state = State::Paused;
                        self.save(&job)?;
                        return Ok(());
                    }
                    Event::Control(command) => {
                        self.apply_control(command);
                        continue;
                    }
                };
                if let Err(error) = outcome {
                    if job.state != State::Publishing {
                        job.state = State::Failed;
                    }
                    job.error = Some(error.to_string());
                    self.save(&job)?;
                }
            } else {
                tokio::select! { biased; _ = stopped.changed() => {}, Some(command) = commands.recv() => self.apply_control(command), _ = self.wake.notified() => {} }
            }
        }
    }
    /// The manager exclusively owns admission, cancellation and join receipts.
    /// Transfers own their Job snapshots; a control ACK follows writer join/drop.
    async fn run_parallel(
        self: &Arc<Self>,
        stopped: &mut tokio::sync::watch::Receiver<bool>,
        commands: &mut tokio::sync::mpsc::Receiver<Command>,
        limit: usize,
    ) -> Result<()> {
        let client = reqwest::Client::builder()
            .use_rustls_tls()
            .no_proxy()
            .redirect(reqwest::redirect::Policy::none())
            .connect_timeout(Duration::from_secs(15))
            .timeout(Duration::from_secs(60))
            .user_agent("Furchive/0.1 (durable-download-core)")
            .build()
            .map_err(|e| Error::Invalid(e.to_string()))?;
        let mut tasks = tokio::task::JoinSet::new();
        let mut active =
            std::collections::HashMap::<tokio::task::Id, (i64, tokio::task::AbortHandle)>::new();
        let outcome=async{
            loop{
                if *stopped.borrow(){break;}
                let jobs=self.jobs()?;
                for mut job in jobs.iter().filter(|j|j.state==State::Queued).cloned(){
                    if active.len()>=limit{break;}
                    // Native case aliases and Rename suffix publication share a
                    // destination namespace. Reserve conservatively, FIFO across
                    // pending queued/running predecessors; paused jobs hold no writer.
                    let conflicts=|other:&Job|{
                        if other.id==job.id{return false;}
                        let a=self.grant.parent(&other.relative_path).and_then(|(parent,_)|crate::files::identity(&parent).map_err(Into::into));
                        let b=self.grant.parent(&job.relative_path).and_then(|(parent,_)|crate::files::identity(&parent).map_err(Into::into));
                        match (a,b){(Ok(a),Ok(b))=>a==b,_=>true}
                    };
                    if jobs.iter().any(|other|((other.id<job.id&&matches!(other.state,State::Queued|State::Running|State::Publishing))||active.values().any(|(id,_)|*id==other.id))&&conflicts(other)){continue;}
                    job.state=State::Running;self.save(&job)?;
                    let engine=self.clone();let http=client.clone();let id=job.id;
                    let handle=tasks.spawn(async move{
                        if let Err(error)=engine.transfer(&http,&mut job).await{if job.state!=State::Publishing{job.state=State::Failed;}job.error=Some(error.to_string());engine.save(&job)?;}
                        Ok::<_,Error>(())
                    });active.insert(handle.id(),(id,handle));
                }
                tokio::select!{biased;
                    _=stopped.changed()=>{},
                    Some(command)=commands.recv()=>{
                        if matches!(command.control,Control::Resume)&&active.values().any(|(id,_)|*id==command.id){
                            let result=self.jobs()?.into_iter().find(|j|j.id==command.id).ok_or_else(||Error::Invalid("unknown job".into()));let _=command.reply.send(result);continue;
                        }
                        if !matches!(command.control,Control::Resume){
                            if let Some((_,handle))=active.values().find(|(id,_)|*id==command.id){
                                let target=handle.id();handle.abort();
                                while active.contains_key(&target){
                                    if let Some(result)=tasks.join_next_with_id().await{match result{Ok((id,result))=>{active.remove(&id);result?;},Err(error)=>{active.remove(&error.id());if !error.is_cancelled(){return Err(Error::Stopped);}}}}
                                }
                            }
                        }
                        self.apply_control(command);
                    },
                    result=tasks.join_next_with_id(),if !active.is_empty()=>{
                        if let Some(result)=result{match result{Ok((id,result))=>{active.remove(&id);result?;},Err(error)=>{active.remove(&error.id());return Err(Error::Stopped);}}}
                    },
                    _=self.wake.notified()=>{}
                }
            }Ok(())
        }.await;
        tasks.abort_all();
        while tasks.join_next().await.is_some() {}
        // Persist only after all affected writer futures have been dropped.
        for mut job in self.jobs()? {
            if job.state == State::Running {
                job.state = State::Paused;
                self.save(&job)?;
            }
        }
        outcome
    }
    async fn transfer(&self, client: &reqwest::Client, job: &mut Job) -> Result<()> {
        if job.intent.policy == DuplicatePolicy::Skip && self.grant.exists(&job.relative_path)? {
            job.state = State::Skipped;
            self.save(job)?;
            return self.grant.remove_part(job.id);
        }
        let url = crate::model::validate_url(&job.intent.url)?;
        #[cfg(any(test, feature = "fixture-http"))]
        let url = if let Some(origin) = &self.loopback {
            url::Url::parse(&format!("{origin}{}", url.path())).unwrap()
        } else {
            url
        };
        let mut part = self.grant.part(job.id)?;
        if part.metadata()?.len() < job.bytes || job.validator.is_none() {
            job.bytes = 0;
            job.validator = None;
            job.total = None;
        }
        part.set_len(job.bytes)?;
        part.seek(SeekFrom::Start(job.bytes))?;
        part.sync_all()?;
        self.save(job)?;
        let offset = job.bytes;
        let mut request = client
            .get(url)
            .header(reqwest::header::ACCEPT_ENCODING, "identity");
        if offset > 0 {
            let validator = job.validator.as_ref().unwrap().split_once(':').unwrap().1;
            request = request
                .header(reqwest::header::RANGE, format!("bytes={offset}-"))
                .header(reqwest::header::IF_RANGE, validator);
        }
        let mut response = request
            .send()
            .await
            .map_err(|e| Error::Invalid(e.to_string()))?;
        if response
            .headers()
            .get(reqwest::header::CONTENT_ENCODING)
            .is_some_and(|v| v != "identity")
        {
            return Err(Error::Invalid("non-identity Content-Encoding".into()));
        }
        if response.status() == reqwest::StatusCode::PARTIAL_CONTENT && offset > 0 {
            let range = response
                .headers()
                .get(reqwest::header::CONTENT_RANGE)
                .and_then(|v| v.to_str().ok())
                .ok_or_else(|| Error::Invalid("missing Content-Range".into()))?;
            let (start, end, total) = parse_range(range)?;
            if total > i64::MAX as u64
                || start != offset
                || end.checked_add(1) != Some(total)
                || job.total.is_some_and(|n| n != total)
                || validator(response.headers()) != job.validator
                || response
                    .content_length()
                    .is_some_and(|n| n != total - start)
            {
                return Err(Error::Invalid("mismatched resume response".into()));
            }
            job.total = Some(total);
        } else if response.status() == reqwest::StatusCode::RANGE_NOT_SATISFIABLE && offset > 0 {
            let total = response
                .headers()
                .get(reqwest::header::CONTENT_RANGE)
                .and_then(|v| v.to_str().ok())
                .and_then(|s| s.strip_prefix("bytes */"))
                .and_then(|s| s.parse::<u64>().ok());
            if total != Some(offset)
                || job.total != total
                || validator(response.headers()) != job.validator
            {
                return Err(Error::Invalid("unproven 416 completion".into()));
            }
            // Never consume an error page as media bytes.
            if job.intent.size.is_some_and(|n| n != offset) {
                return Err(Error::Invalid("size mismatch".into()));
            }
            part.sync_all()?;
            return self.publish(job, &mut part);
        } else if response.status() == reqwest::StatusCode::OK {
            // An ignored Range or changed validator is a fresh representation, never append.
            job.bytes = 0;
            part.set_len(0)?;
            part.seek(SeekFrom::Start(0))?;
            job.total = response.content_length();
            if job.total.is_some_and(|n| n > i64::MAX as u64) {
                job.total = None;
                return Err(Error::Invalid(
                    "HTTP length exceeds supported maximum".into(),
                ));
            }
            job.validator = validator(response.headers());
        } else {
            return Err(Error::Invalid(format!("HTTP {}", response.status())));
        }
        part.sync_all()?;
        self.save(job)?;
        while let Some(chunk) = response
            .chunk()
            .await
            .map_err(|e| Error::Invalid(e.to_string()))?
        {
            let next = job
                .bytes
                .checked_add(chunk.len() as u64)
                .ok_or_else(|| Error::Invalid("byte count overflow".into()))?;
            if next > i64::MAX as u64
                || job.total.is_some_and(|n| next > n)
                || job.intent.size.is_some_and(|n| next > n)
            {
                return Err(Error::Invalid("response exceeds expected size".into()));
            }
            part.write_all(&chunk)?;
            part.sync_data()?;
            job.bytes = next;
            self.save(job)?;
        }
        if job.intent.size.is_some_and(|n| n != job.bytes)
            || job.total.is_some_and(|n| n != job.bytes)
        {
            return Err(Error::Invalid("size mismatch".into()));
        }
        part.sync_all()?;
        self.publish(job, &mut part)?;
        Ok(())
    }
    pub(crate) fn reconcile_publication(&self, job: &mut Job) -> Result<()> {
        let (identity, digest) = job
            .publication
            .clone()
            .ok_or_else(|| Error::Invalid("missing publication receipt".into()))?
            .parts();
        if self.grant.part_exists(job.id)? {
            let mut part = self.grant.part(job.id)?;
            if crate::files::identity(&part)? != identity
                || validate_file(&mut part, job)? != digest
            {
                return Err(Error::Invalid("publication part identity changed".into()));
            }
            job.error = None;
            return self.publish(job, &mut part);
        }
        let mut completed = self.grant.destination(&job.relative_path)?;
        if crate::files::identity(&completed)? != identity
            || validate_file(&mut completed, job)? != digest
        {
            return Err(Error::Invalid(
                "published destination identity changed".into(),
            ));
        }
        let (parent, _) = self.grant.parent(&job.relative_path)?;
        crate::files::sync_directory(&parent)?;
        crate::files::sync_directory(&self.grant.stage)?;
        job.state = State::Completed;
        job.error = None;
        self.save(job)
    }
    fn publish(&self, job: &mut Job, part: &mut std::fs::File) -> Result<()> {
        let sha = validate_file(part, job)?;
        let publication = crate::model::Publication::Native {
            identity: crate::files::identity(part)?,
            digest: sha,
        };
        let original = job.intent.relative.clone();
        for suffix in 0..10_000 {
            if suffix > 0 {
                let path = std::path::Path::new(&original);
                let stem = path.file_stem().unwrap().to_string_lossy();
                let ext = path
                    .extension()
                    .map(|e| format!(".{}", e.to_string_lossy()))
                    .unwrap_or_default();
                let suffix_text = format!(" ({suffix})");
                let available = 255usize
                    .checked_sub(suffix_text.len() + ext.len())
                    .ok_or_else(|| {
                        Error::Invalid("extension leaves no room for duplicate name".into())
                    })?;
                let mut cut = stem.len().min(available);
                while !stem.is_char_boundary(cut) {
                    cut -= 1;
                }
                let name = format!("{}{}{}", &stem[..cut], suffix_text, ext);
                let candidate = match original.rsplit_once('/') {
                    Some((parent, _)) => format!("{parent}/{name}"),
                    None => name,
                };
                crate::files::relative(&candidate)?;
                self.check_destination(&candidate)?;
                job.relative_path = candidate;
            }
            self.check_destination(&job.relative_path)?;
            let (dir, name) = self.grant.parent(&job.relative_path)?;
            // Persist publication identity/digest anew for every collision candidate.
            job.publication = Some(publication.clone());
            job.state = State::Publishing;
            self.save(job)?;
            #[cfg(test)]
            if self.crash_at.load(Ordering::SeqCst) == 1
                || (suffix > 0 && self.crash_at.load(Ordering::SeqCst) == 4)
            {
                return Err(Error::Invalid("injected crash before rename".into()));
            }
            match self.grant.publish(
                job.id,
                part,
                &dir,
                &name,
                job.intent.policy == DuplicatePolicy::Overwrite,
            ) {
                Ok(()) => {
                    #[cfg(test)]
                    if self.crash_at.load(Ordering::SeqCst) == 2 {
                        return Err(Error::Invalid("injected crash after rename".into()));
                    }
                    crate::files::sync_directory(&dir)?;
                    crate::files::sync_directory(&self.grant.stage)?;
                    #[cfg(test)]
                    if self.crash_at.load(Ordering::SeqCst) == 3 {
                        return Err(Error::Invalid("injected crash after directory sync".into()));
                    }
                    job.state = State::Completed;
                    return self.save(job);
                }
                Err(e)
                    if e.kind() == std::io::ErrorKind::AlreadyExists
                        && job.intent.policy == DuplicatePolicy::Rename =>
                {
                    // NOREPLACE refused before mutation: no ambiguous publication exists.
                    // Clear the old commit intent before constructing another candidate.
                    job.state = State::Running;
                    job.publication = None;
                    self.save(job)?;
                    continue;
                }
                Err(e)
                    if e.kind() == std::io::ErrorKind::AlreadyExists
                        && job.intent.policy == DuplicatePolicy::Skip =>
                {
                    job.state = State::Skipped;
                    self.save(job)?;
                    return self.grant.remove_part(job.id);
                }
                Err(e) => return Err(Error::Io(e)),
            }
        }
        Err(Error::Invalid("rename collision limit reached".into()))
    }
}
