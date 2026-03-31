async function fetchSpaces(){
  try{
    // Prefer rooms if present
    let res = await fetch('/api/rooms', { credentials: 'include' });
    if(res.ok){
      const rooms = await res.json();
      if(Array.isArray(rooms) && rooms.length) return rooms.map(r=>({ id: r.id, name: r.name, capacity: r.capacity }));
    }
    res = await fetch('/api/spaces', { credentials: 'include' });
    if(!res.ok) throw new Error('Failed to load');
    return await res.json();
  }catch(e){
    console.error(e);
    return [];
  }
}

function renderMap(spaces){
  const container = document.getElementById('mapContainer');
  container.innerHTML = '';
  const svgNS = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(svgNS,'svg');
  svg.setAttribute('class','space-map');
  svg.setAttribute('viewBox','0 0 1000 420');

  const cols = 4;
  const rows = Math.ceil(spaces.length / cols) || 1;
  const pad = 20;
  const cellW = (1000 - (pad*(cols+1))) / cols;
  const cellH = (360 - (pad*(rows+1))) / rows;

  spaces.forEach((s, i)=>{
    const col = i % cols;
    const row = Math.floor(i/cols);
    const x = pad + col*(cellW + pad);
    const y = pad + row*(cellH + pad);

    const rect = document.createElementNS(svgNS,'rect');
    rect.setAttribute('x', x);
    rect.setAttribute('y', y);
    rect.setAttribute('rx', 8);
    rect.setAttribute('ry', 8);
    rect.setAttribute('width', cellW);
    rect.setAttribute('height', cellH);
    rect.classList.add('space-rect');
    if(s.capacity && s.capacity < 2) rect.classList.add('small-room');
    rect.addEventListener('click', (e)=>{
      e.stopPropagation();
      console.log('[map] rect click', s.id);
      // dispatch an event so booking.js can react even if script load order differs
      try { document.dispatchEvent(new CustomEvent('space:selected', { detail: { id: s.id } })); } catch (e) { console.warn('[map] dispatch space:selected failed', e); }
      // Prefer calling the booking API directly to avoid issues if selectSpace is
      // shadowed or otherwise not executing as expected in some environments.
      if (window.showBookingForSpace) {
        try {
          console.log('[map] calling showBookingForSpace');
          window.showBookingForSpace(s.id);
          // if booking panel remains hidden (race or CSS), show a simple overlay fallback
          setTimeout(()=>{
            try{
              const bc = document.getElementById('bookingContainer');
              const cs = bc && window.getComputedStyle(bc);
              if (!bc || (cs && (cs.display === 'none' || cs.visibility === 'hidden'))){
                console.warn('[map] bookingContainer still hidden — showing fallback overlay');
                // create simple overlay
                let overlay = document.getElementById('mapBookingFallback');
                if (!overlay){
                  overlay = document.createElement('div');
                  overlay.id = 'mapBookingFallback';
                  overlay.style.position = 'fixed';
                  overlay.style.left = '0'; overlay.style.top = '0'; overlay.style.right = '0'; overlay.style.bottom = '0';
                  overlay.style.background = 'rgba(0,0,0,0.5)'; overlay.style.zIndex = '2000'; overlay.style.display = 'block';
                  const inner = document.createElement('div');
                  inner.style.position = 'absolute'; inner.style.left = '50%'; inner.style.top = '50%'; inner.style.transform = 'translate(-50%,-50%)';
                  inner.style.width = '90%'; inner.style.maxWidth = '900px'; inner.style.maxHeight = '80%'; inner.style.overflow = 'auto';
                  inner.style.background = '#fff'; inner.style.borderRadius = '8px'; inner.style.padding = '12px';
                  const closeBtn = document.createElement('button');
                  closeBtn.textContent = 'Close'; closeBtn.className = 'btn btn-sm btn-outline-secondary';
                  closeBtn.style.float = 'right'; closeBtn.addEventListener('click', ()=>{ overlay.remove(); location.reload(); });
                  inner.appendChild(closeBtn);
                  // move bookingPanel into overlay
                  if (bc) inner.appendChild(bc);
                  overlay.appendChild(inner);
                  document.body.appendChild(overlay);
                }
              }
            }catch(e){ console.warn('[map] fallback overlay failed', e); }
          }, 300);
          return;
        } catch (err) {
          console.warn('[map] showBookingForSpace failed', err);
        }
      }
      // fallback to the original handler
      try { selectSpace(s.id); } catch (e) { console.error('[map] selectSpace failed', e); }
    });
    const title = document.createElementNS(svgNS,'title');
    title.textContent = `${s.name} — capacity ${s.capacity}`;
    rect.appendChild(title);
    svg.appendChild(rect);
    rect.setAttribute('data-space-id', s.id);

    const name = document.createElementNS(svgNS,'text');
    name.setAttribute('x', x + 12);
    name.setAttribute('y', y + 28);
    name.setAttribute('class','space-label');
    name.textContent = s.name;
    name.setAttribute('pointer-events', 'none');
    svg.appendChild(name);

    const cap = document.createElementNS(svgNS,'text');
    cap.setAttribute('x', x + 12);
    cap.setAttribute('y', y + 48);
    cap.setAttribute('class','space-cap');
    cap.textContent = `Capacity: ${s.capacity}`;
    cap.setAttribute('pointer-events', 'none');
    svg.appendChild(cap);
  });

  container.appendChild(svg);
  document.getElementById('count').textContent = `${spaces.length} spaces`;
}

function clearSelection(){
  document.querySelectorAll('.space-rect.selected').forEach(el=>el.classList.remove('selected'));
  document.querySelectorAll('.space-list li.selected').forEach(el=>el.classList.remove('selected'));
}

function selectSpace(id){
  clearSelection();
  const rect = document.querySelector(`.space-rect[data-space-id='${id}']`);
  const li = document.querySelector(`#spaceList li[data-space-id='${id}']`);
  if(rect) rect.classList.add('selected');
  if(li) li.classList.add('selected');
  const spaces = window.__spacesCache || [];
  const s = spaces.find(x=>x.id==id);
  const detail = document.getElementById('detailPanel');
  if(s && detail){
    console.log('[map] selectSpace', s.id);
    // show only basic info and a link to the booking panel. Detailed week calendar
    // is hidden by default on the map page and will be revealed when a room is clicked.
    detail.innerHTML = `
      <div class="mb-2"><strong>${s.name}</strong><div class="text-muted small">Capacity: ${s.capacity}</div></div>
      <div class="d-flex gap-2 mt-2">
        <button id="previewCalendarBtn" class="btn btn-sm btn-outline-primary">Preview calendar</button>
        <a href="/Booking?spaceId=${encodeURIComponent(s.id)}" class="btn btn-sm btn-primary">Book</a>
      </div>
    `;
    // wire preview button
    setTimeout(()=>{
      const preview = document.getElementById('previewCalendarBtn');
      if (preview) preview.addEventListener('click', ()=>{
        if (window.showBookingForSpace) {
          try { window.showBookingForSpace(s.id); } catch(e){ console.warn('preview showBookingForSpace failed', e); }
        } else {
          document.dispatchEvent(new CustomEvent('space:selected',{ detail: { id: s.id } }));
        }
      });
    }, 10);

    // Open booking UI in a Bootstrap modal so it's always visible to the user.
    const bookingPanel = document.getElementById('bookingContainer');
    if (bookingPanel) {
      console.log('[map] bookingPanel exists, preparing modal');
      try {
        // remove any pre-existing modal instance
        const prev = document.getElementById('mapBookingModal');
        if (prev) prev.remove();

        // create modal wrapper
        const modal = document.createElement('div');
        modal.id = 'mapBookingModal';
        modal.className = 'modal fade';
        modal.tabIndex = -1;
        modal.innerHTML = `
          <div class="modal-dialog modal-lg modal-dialog-centered">
            <div class="modal-content">
              <div class="modal-header">
                <h5 class="modal-title">Booking — ${s.name}</h5>
                <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
              </div>
              <div class="modal-body" id="mapBookingModalBody"></div>
            </div>
          </div>`;
        document.body.appendChild(modal);

        // move the existing bookingPanel into modal body
        const modalBody = document.getElementById('mapBookingModalBody');
        const originalParent = bookingPanel.parentElement;
        const originalNext = bookingPanel.nextSibling;
        modalBody.appendChild(bookingPanel);

        // create a container for the full calendar inside the modal (below the booking panel)
        let fcModal = document.getElementById('fullCalendarModal');
        if (!fcModal) {
          fcModal = document.createElement('div');
          fcModal.id = 'fullCalendarModal';
          fcModal.style.minHeight = '320px';
          fcModal.className = 'mt-3';
          modalBody.appendChild(fcModal);
        }

        // ensure visible
        if (bookingPanel.hasAttribute && bookingPanel.hasAttribute('style')) bookingPanel.removeAttribute('style');
        bookingPanel.style.setProperty('display','block','important');

        // initialize booking UI for the selected space
        console.log('[map] invoking showBookingForSpace?', !!window.showBookingForSpace);
        if (window.showBookingForSpace) {
          try { window.showBookingForSpace(s.id); } catch (e) { console.warn('showBookingForSpace failed', e); }
        }

        // initialize the full calendar inside the modal (on-demand)
        console.log('[map] initFullCalendar available?', !!window.initFullCalendar);
        if (window.initFullCalendar) {
          try { window.initFullCalendar('fullCalendarModal'); } catch (e) { console.warn('initFullCalendar failed', e); }
        }

        // show modal via Bootstrap
        if (window.bootstrap && typeof bootstrap.Modal === 'function') {
          console.log('[map] showing bootstrap modal');
          const bsModal = new bootstrap.Modal(modal);
          bsModal.show();
        } else {
          console.warn('[map] bootstrap.Modal not available');
        }

        // restore bookingPanel when modal hidden
        modal.addEventListener('hidden.bs.modal', ()=>{
          try {
            if (originalNext) originalParent.insertBefore(bookingPanel, originalNext);
            else originalParent.appendChild(bookingPanel);
            bookingPanel.style.setProperty('display','none','important');
            modal.remove();
          } catch(e){ console.warn('Failed to restore booking panel', e); }
        });
      } catch (err) {
        console.error('[map] error preparing booking modal', err);
      }
    }
  }
}

// week state for map panel
let mapWeekStart = startOfWeek(new Date());

function startOfWeek(d){ const dt = new Date(d); const day = dt.getDay(); const diff = (day + 6) % 7; dt.setDate(dt.getDate()-diff); dt.setHours(0,0,0,0); return dt; }
function addDays(d,n){ const r = new Date(d); r.setDate(r.getDate()+n); return r; }

async function renderSpaceWeek(spaceId, capacity){
  const grid = document.getElementById('mapWeekView');
  const lbl = document.getElementById('mapWeekLabel');
  if(!grid) return;
  grid.innerHTML = 'Loading...';
  console.log('renderSpaceWeek', { spaceId, capacity, mapWeekStart });
  const days = [];
  for(let i=0;i<7;i++) days.push(addDays(mapWeekStart,i));
  lbl.textContent = `${days[0].toISOString().slice(0,10)} → ${days[6].toISOString().slice(0,10)}`;

  // fetch reservations per day
  const promises = days.map(d => fetch(`/api/reservations/space?spaceId=${spaceId}&date=${d.toISOString().slice(0,10)}`, { credentials: 'include' })
    .then(async r=> {
      if (!r.ok) {
        const txt = await r.text().catch(()=>null);
        throw new Error(`HTTP ${r.status} ${r.statusText} ${txt||''}`);
      }
      return r.json();
    }));
  let results;
  try{
    results = await Promise.all(promises);
  } catch(err){
    console.error('Failed to load reservations', err);
    grid.innerHTML = `<div class="text-danger">Failed to load reservations: ${err.message}</div>`;
    // if unauthorized, redirect to login
    if (String(err.message).includes('HTTP 401')){
      window.location.href = '/Login';
    }
    return;
  }

  grid.innerHTML = '';
  const table = document.createElement('table'); table.className = 'table table-sm';
  const thead = document.createElement('thead');
  const hr = document.createElement('tr');
  hr.innerHTML = '<th>Hour</th>' + days.map(d=>`<th>${d.toLocaleDateString(undefined,{weekday:'short',month:'short',day:'numeric'})}</th>`).join('');
  thead.appendChild(hr); table.appendChild(thead);
  const tbody = document.createElement('tbody');
  for(let h=7; h<=21; h++){
    const tr = document.createElement('tr');
    const th = document.createElement('th'); th.textContent = `${h}:00`; tr.appendChild(th);
    for(let di=0; di<7; di++){
      const cell = document.createElement('td');
      const dayBookings = results[di] || [];
      const bookingsAtHour = dayBookings.filter(b=>{
        const s = b.startHour ?? (b.start? new Date(b.start).getHours():0);
        const hrs = b.hours ?? Math.max(1, Math.round((b.end && b.start) ? (new Date(b.end)-new Date(b.start))/3600000 : 1));
        return (h >= s) && (h < s+hrs) && (b.status === 'Booked');
      });
      const count = bookingsAtHour.length;
      if(count >= capacity) { cell.className = 'bg-danger text-white'; cell.textContent = '×'; }
      else if(count > 0) { cell.className = 'bg-warning text-dark'; cell.textContent = String(count); }
      else { cell.className = 'bg-success text-white'; cell.textContent = '✓'; }

      if(count>0){
        const bk = bookingsAtHour[0];
        const owner = bk.ownerName || ('User#'+(bk.ownerId||'?'));
        const st = bk.status || 'Booked';
        cell.title = `${owner} — ${st}`;
      }

      // clicking a cell opens Booking page with prefilled params
      cell.style.cursor = 'pointer';
      cell.addEventListener('click', ()=>{
        const selectedDate = days[di].toISOString().slice(0,10);
        // navigate to booking page and prefill via query string
        window.location.href = `/Booking?spaceId=${encodeURIComponent(spaceId)}&date=${encodeURIComponent(selectedDate)}&start=${h}`;
      });

      tr.appendChild(cell);
    }
    tbody.appendChild(tr);
  }
  table.appendChild(tbody);
  grid.appendChild(table);
}

async function loadAndRender(){
  const spaces = await fetchSpaces();
  window.__spacesCache = spaces;
  renderList(spaces);
  renderMap(spaces);
}

function renderList(spaces){
  const ul = document.getElementById('spaceList');
  ul.innerHTML = '';
  spaces.forEach(s=>{
    const li = document.createElement('li');
    li.textContent = `${s.name} (cap ${s.capacity})`;
    li.dataset.spaceId = s.id;
    li.dataset.name = s.name;
    li.addEventListener('mouseenter', ()=>{ const r = document.querySelector(`.space-rect[data-space-id='${s.id}']`); if(r) r.classList.add('selected'); const detail = document.getElementById('detailPanel'); if(detail) detail.innerHTML = `<strong>${s.name}</strong><div>Capacity: ${s.capacity}</div>`; });
    li.addEventListener('mouseleave', ()=>{ clearSelection(); });
    li.addEventListener('click', ()=> selectSpace(s.id));
    ul.appendChild(li);
  });
}

document.addEventListener('DOMContentLoaded', ()=>{
  document.getElementById('refresh').addEventListener('click', loadAndRender);
  const search = document.getElementById('search');
  if(search){ search.addEventListener('input', (e)=>{ const q = e.target.value.toLowerCase(); document.querySelectorAll('#spaceList li').forEach(li=>{ const name = li.dataset.name.toLowerCase(); li.style.display = name.includes(q) ? '' : 'none'; }); }); }
  loadAndRender();
});
