document.addEventListener('DOMContentLoaded', () => {
  (async function(){
    const container = document.getElementById('bookingContainer');
    if (!container) return;

    const spaceSel = document.getElementById('spaceSelect');
    const dateInput = document.getElementById('dateInput');
    const startInput = document.getElementById('startInput');
    const hoursInput = document.getElementById('hoursInput');
    const bookBtn = document.getElementById('bookBtn');
    const msg = document.getElementById('bookingMsg');

    let rooms = [];

    async function loadSpaces(){
      const res = await fetch('/api/rooms', { credentials: 'include' });
      if (!res.ok) return;
      rooms = await res.json();
      spaceSel.innerHTML = '';
      for (const s of rooms){
        const opt = document.createElement('option');
        opt.value = s.id; opt.textContent = `${s.name} (cap ${s.capacity})`;
        spaceSel.appendChild(opt);
      }
      spaceSel.addEventListener('change', ()=> { fetchBookedSlots(); renderWeek(); });
      // if page was opened with query params (from map), pre-select values
      try {
        const params = new URLSearchParams(window.location.search);
        const sid = params.get('spaceId');
        const d = params.get('date');
        const s = params.get('start');
        if (sid) {
          // set if option exists
          const opt = Array.from(spaceSel.options).find(o=>o.value===sid || o.value===String(Number(sid)));
        if (opt) {
          spaceSel.value = opt.value;
          // trigger change handlers only if booking panel is visible
          if (container.style.display !== 'none') { fetchBookedSlots(); renderWeek(); }
        }
        }
        if (d) dateInput.value = d;
        if (s) startInput.value = s;
      } catch (e) { /* ignore */ }
    }

    // react to a global event from the map if fired
    document.addEventListener('space:selected', (ev)=>{
      try{
        const id = ev?.detail?.id;
        if (id) {
          console.log('[booking] space:selected event received', id);
          showBookingForSpace(id);
        }
      }catch(e){ console.warn('[booking] space:selected handler failed', e); }
    });

    // Warm-up FullCalendar to avoid race conditions
    (function warmFullCalendar(){
      function tryInit(){
        if (!window.FullCalendar) return false;
        try {
          if (!document.getElementById('fullCalendarBootstrap')){
            const el = document.createElement('div');
            el.id = 'fullCalendarBootstrap';
            el.style.display = 'none';
            document.body.appendChild(el);
            initFullCalendar('fullCalendarBootstrap');
            console.log('[booking] FullCalendar warm-up completed');
          }
        } catch (err){ console.warn('[booking] FullCalendar warm-up failed', err); }
        return true;
      }
      if (!tryInit()){
        const t = setInterval(()=>{ if (tryInit()) clearInterval(t); }, 200);
        window.addEventListener('load', ()=>{ tryInit(); });
      }
    })();

    bookBtn.addEventListener('click', async (e)=>{
      e.preventDefault();
      const payload = {
        spaceId: parseInt(spaceSel.value,10),
        date: dateInput.value,
        startHour: parseInt(startInput.value,10),
        hours: parseInt(hoursInput.value,10)
      };
      const res = await fetch('/api/reservations', { method: 'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(payload), credentials: 'include' });
      if (res.ok){
        msg.textContent = 'Booked ✓'; msg.style.color='green';
        await fetchBookedSlots(); renderWeek();
      } else if (res.status === 409) {
        const j = await res.json().catch(()=>null);
        msg.textContent = j?.error || 'Time conflict';
        msg.style.color = 'red';
      } else {
        const j = await res.json().catch(()=>null);
        msg.textContent = j?.error || 'Booking failed'; msg.style.color='red';
      }
    });

    async function fetchBookedSlots(){
      const out = document.getElementById('bookedList') || createBookedList();
      out.innerHTML = 'Loading...';
      const sp = parseInt(spaceSel.value,10);
      if (!sp) { out.innerHTML = 'No space selected'; return; }
      const d = dateInput.value || new Date().toISOString().slice(0,10);
      const res = await fetch(`/api/reservations/space?spaceId=${sp}&date=${encodeURIComponent(d)}`, { credentials: 'include' });
      if (!res.ok) { out.innerHTML = 'Failed to load bookings'; return; }
      const items = await res.json();
      out.innerHTML = '';
      if (!items.length) { out.innerHTML = 'No bookings for this date'; return; }
      const ul = document.createElement('ul');
      items.forEach(it=>{
        const li = document.createElement('li');
        const start = it.startHour ?? (it.start ? new Date(it.start).getHours() : 0);
        const hours = it.hours ?? Math.max(1, Math.round((new Date(it.end)-new Date(it.start))/3600000));
        li.textContent = `${it.date || ''} ${start}:00 for ${hours}h — ${it.ownerName || it.status || ''}`;
        ul.appendChild(li);
      });
      out.appendChild(ul);
    }

    function createBookedList(){
      const el = document.createElement('div');
      el.id = 'bookedList';
      const form = document.querySelector('#bookingContainer form') || document.getElementById('bookingForm');
      (form || document.querySelector('#bookingContainer')).appendChild(el);
      return el;
    }

    dateInput.value = new Date().toISOString().slice(0,10);
    startInput.value = '9'; hoursInput.value = '1';

    // week navigation state
    let weekStart = startOfWeek(new Date(dateInput.value));

    document.getElementById('prevWeek').addEventListener('click', ()=> { weekStart = addDays(weekStart, -7); renderWeek(); });
    document.getElementById('nextWeek').addEventListener('click', ()=> { weekStart = addDays(weekStart, 7); renderWeek(); });
    dateInput.addEventListener('change', ()=> { weekStart = startOfWeek(new Date(dateInput.value)); renderWeek(); fetchBookedSlots(); });

    await loadSpaces();
    // do not auto-render bookings on page load when the booking panel is hidden;
    // render only when the panel is visible (e.g. after user clicks a room)
    if (container.style.display !== 'none') {
      await fetchBookedSlots();
      await renderWeek();
    }
    // FullCalendar is intentionally disabled on the map pages to keep the UI compact.
    // (Do not auto-initialize the full calendar here.)

    // utilities
    function startOfWeek(d){ const dt = new Date(d); const day = dt.getDay(); const diff = (day + 6) % 7; dt.setDate(dt.getDate()-diff); dt.setHours(0,0,0,0); return dt; }
    function addDays(d,n){ const r = new Date(d); r.setDate(r.getDate()+n); return r; }

    // render week availability grid
    async function renderWeek(){
      const grid = document.getElementById('weekView');
      console.log('[booking] renderWeek: visible hours 07:00-21:00');
      const lbl = document.getElementById('weekLabel');
      grid.innerHTML = 'Loading...';
      const sp = parseInt(spaceSel.value,10);
      if (!sp) { grid.innerHTML = 'No space selected'; lbl.textContent = ''; return; }
      const room = rooms.find(r=>r.id == sp) || { capacity: 1 };
      // build days
      const days = [];
      for (let i=0;i<7;i++){ days.push(addDays(weekStart,i)); }
      lbl.textContent = `${days[0].toISOString().slice(0,10)} → ${days[6].toISOString().slice(0,10)}`;

      // fetch bookings for each day in parallel
      const promises = days.map(d => fetch(`/api/reservations/space?spaceId=${sp}&date=${d.toISOString().slice(0,10)}`, { credentials: 'include' }).then(r=> r.ok? r.json(): []));
      const results = await Promise.all(promises);

      grid.innerHTML = '';
      // mark grid container for styling
      grid.classList.add('availability-grid');
      const table = document.createElement('table'); table.className = 'table table-sm table-responsive';
      const thead = document.createElement('thead');
      const headRow = document.createElement('tr');
      headRow.innerHTML = '<th style="width:60px">Hour</th>' + days.map(d=>`<th>${d.toLocaleDateString(undefined,{weekday:'short',month:'short',day:'numeric'})}</th>`).join('');
      thead.appendChild(headRow); table.appendChild(thead);

      const tbody = document.createElement('tbody');
      // show hours from 7:00 to 21:00 only
      for (let h=7; h<=21; h++){
        const tr = document.createElement('tr');
        const th = document.createElement('th'); th.textContent = `${h}:00`; tr.appendChild(th);
        for (let di=0; di<7; di++){
          const cell = document.createElement('td');
          const dayBookings = results[di] || [];
          // bookings that cover this hour
          const bookingsAtHour = dayBookings.filter(b=>{
            const s = b.startHour ?? (b.start? new Date(b.start).getHours():0);
            const hrs = b.hours ?? Math.max(1, Math.round((b.end && b.start) ? (new Date(b.end)-new Date(b.start))/3600000 : 1));
            return (h >= s) && (h < s+hrs) && (b.status === 'Booked');
          });
          const count = bookingsAtHour.length;
          // semantic classes for styling
          if (count >= room.capacity) {
            cell.className = 'occupied full';
            cell.innerHTML = '<span class="cell-icon">✖</span>';
          }
          else if (count > 0) {
            cell.className = 'occupied partial';
            cell.innerHTML = `<span class="cell-count">${count}</span>`;
          }
          else {
            cell.className = 'free';
            cell.innerHTML = '<span class="cell-icon">✔</span>';
          }

          // tooltip and click-to-select
          if (count>0){
            const bk = bookingsAtHour[0];
            const owner = bk.ownerName || ('User#'+(bk.ownerId||'?'));
            const st = bk.status || 'Booked';
            cell.title = `${owner} — ${st}`;
            cell.style.cursor = 'pointer';
            cell.addEventListener('click', ()=>{
              dateInput.value = days[di].toISOString().slice(0,10);
              startInput.value = String(h);
              hoursInput.value = '1';
              document.getElementById('bookingMsg').textContent = `Selected ${dateInput.value} ${h}:00`;
            });
          } else {
            // free cell click selects it too
            cell.style.cursor = 'pointer';
            cell.addEventListener('click', ()=>{
              dateInput.value = days[di].toISOString().slice(0,10);
              startInput.value = String(h);
              hoursInput.value = '1';
              document.getElementById('bookingMsg').textContent = `Selected ${dateInput.value} ${h}:00`;
            });
          }

          tr.appendChild(cell);
        }
        tbody.appendChild(tr);
      }
      table.appendChild(tbody);
      grid.appendChild(table);
      // update legend / capacity indicator
      updateLegend(room.capacity);
    }

    function updateLegend(capacity){
      let legend = document.getElementById('calendarLegend');
      if (!legend){
        legend = document.createElement('div');
        legend.id = 'calendarLegend';
        legend.className = 'mt-2';
        const form = document.querySelector('#bookingContainer') || container;
        (form || container).appendChild(legend);
      }
      legend.innerHTML = `<div class="d-flex gap-2 align-items-center"><span class="legend-item"><span class="badge bg-success me-1">✔</span> Available</span><span class="legend-item"><span class="badge bg-warning text-dark me-1">#</span> Partial</span><span class="legend-item"><span class="badge bg-danger me-1">✖</span> Full (capacity ${capacity})</span></div>`;
    }

    function initFullCalendar(target){
      // Accept an optional target element (DOM node or id). If not provided, look for
      // an element with id 'fullCalendar'. Do not auto-run on page load; this function
      // may be called on-demand (for example when a room is clicked on the map).
      const fcEl = (typeof target === 'string') ? document.getElementById(target) : (target || document.getElementById('fullCalendar'));
      if (!fcEl) return;
      // ensure calendar is only initialized once per element
      if (fcEl.dataset.inited) return;
      if (!window.FullCalendar) {
        // FullCalendar library not loaded; leave as no-op
        console.warn('[booking] FullCalendar not available');
        return;
      }
      fcEl.dataset.inited = '1';
      console.log('[booking] initFullCalendar: setting slotMinTime=07:00, slotMaxTime=21:00');
      let calendar;
      const options = {
        initialView: 'timeGridWeek',
        nowIndicator: true,
        selectable: true,
        selectMirror: true,
        // add a custom button to open the Booking page for the current space/date
        customButtons: {
          openBooking: {
            text: 'Open in Booking',
            click: function(){
              try {
                const sp = parseInt(spaceSel.value,10) || '';
                const d = (calendar && calendar.getDate) ? calendar.getDate().toISOString().slice(0,10) : (new Date().toISOString().slice(0,10));
                const start = 9;
                window.location.href = `/Booking?spaceId=${encodeURIComponent(sp)}&date=${encodeURIComponent(d)}&start=${encodeURIComponent(start)}`;
              } catch(e){ console.warn('openBooking failed', e); }
            }
          }
        },
        headerToolbar: { left: 'openBooking prev,next today', center: 'title', right: 'timeGridWeek,timeGridDay,dayGridMonth' },
        slotMinTime: '07:00:00', slotMaxTime: '21:00:00',
        height: 'auto',
        dayMaxEventRows: true,
        events: async function(fetchInfo, successCallback, failureCallback){
          try {
            const sp = parseInt(spaceSel.value,10);
            if (!sp) { successCallback([]); return; }
            const start = new Date(fetchInfo.start);
            const end = new Date(fetchInfo.end);
            const events = [];
            // small palette to color events by owner
            const palette = ['#7c3aed','#06b6d4','#f97316','#ef4444','#10b981','#f59e0b','#6366f1'];
            // iterate days in range
            for (let d = new Date(start); d < end; d.setDate(d.getDate()+1)){
              const day = d.toISOString().slice(0,10);
              const res = await fetch(`/api/reservations/space?spaceId=${sp}&date=${day}`, { credentials: 'include' });
              if (!res.ok) continue;
              const items = await res.json();
              items.forEach(it => {
                const s = it.start ? new Date(it.start) : new Date(`${it.date}T${String(it.startHour).padStart(2,'0')}:00:00Z`);
                const e = it.end ? new Date(it.end) : new Date(s.getTime() + ((it.hours||1)*3600000));
                const ownerId = it.ownerId || 0;
                const color = palette[ownerId % palette.length];
                events.push({ id: it.id, title: it.ownerName || (it.status||'Booked'), start: s, end: e, backgroundColor: color, borderColor: color, extendedProps: it });
              });
            }
            successCallback(events);
          } catch (err){ failureCallback(err); }
        },
        select: function(selectInfo){
          // user dragged a time range -> either prefill form or navigate to Booking page
          const start = selectInfo.start; // Date
          const end = selectInfo.end;
          const date = start.toISOString().slice(0,10);
          const hour = start.getUTCHours();
          const hours = Math.max(1, Math.round((end - start) / 3600000));
          // if this calendar is the modal preview, navigate to the Booking page
          if (fcEl && (fcEl.id === 'fullCalendarModal' || fcEl.id === 'fullCalendarMap')){
            const sp = parseInt(spaceSel.value,10) || '';
            window.location.href = `/Booking?spaceId=${encodeURIComponent(sp)}&date=${encodeURIComponent(date)}&start=${encodeURIComponent(hour)}&hours=${encodeURIComponent(hours)}`;
            return;
          }
          // otherwise prefill the compact booking form
          dateInput.value = date;
          startInput.value = String(hour);
          hoursInput.value = String(hours);
          document.getElementById('bookingMsg').textContent = `Selected ${date} ${hour}:00 for ${hours}h`;
          calendar.unselect();
        },
        eventClick: function(info){
          // clicking an event fills the booking inputs for editing/replicating
          const p = info.event.extendedProps || {};
          const s = info.event.start;
          const date = s.toISOString().slice(0,10);
          const hour = s.getUTCHours();
          const hours = Math.max(1, Math.round((info.event.end - s)/3600000));
          // if in modal preview navigate to Booking page for this time
          if (fcEl && (fcEl.id === 'fullCalendarModal' || fcEl.id === 'fullCalendarMap')){
            const sp = parseInt(spaceSel.value,10) || '';
            window.location.href = `/Booking?spaceId=${encodeURIComponent(sp)}&date=${encodeURIComponent(date)}&start=${encodeURIComponent(hour)}&hours=${encodeURIComponent(hours)}`;
            return;
          }
          dateInput.value = date;
          startInput.value = String(hour);
          hoursInput.value = String(hours);
          document.getElementById('bookingMsg').textContent = `Event selected: ${info.event.title}`;
        },
        eventDidMount: function(info){
          // attach bootstrap popover with richer details
          const props = info.event.extendedProps || {};
          const owner = props.ownerName || 'Unknown';
          const status = props.status || 'Booked';
          const total = props.total ?? props.Total_Amount ?? '';
          const t = `<div style="min-width:200px"><strong>${info.event.title}</strong><div class=\"text-muted small\">${status}</div><div style=\"margin-top:6px\">${new Date(info.event.start).toLocaleString()} - ${new Date(info.event.end).toLocaleString()}</div><div class=\"mt-2 small\"><strong>Owner:</strong> ${owner}</div><div class=\"small\"><strong>Amount:</strong> ${total}</div></div>`;
          // use popper-based bootstrap popover
          new bootstrap.Popover(info.el, { content: t, html: true, trigger: 'hover', placement: 'auto' });
        }
      };
      calendar = new FullCalendar.Calendar(fcEl, options);
      calendar.render();
      // re-load when space changes
      spaceSel.addEventListener('change', ()=> calendar.refetchEvents());
    }

    // expose API for other scripts to show booking UI for a space
    window.initFullCalendar = initFullCalendar;

    window.showBookingForSpace = async function(spaceId){
      console.log('[booking] showBookingForSpace()', spaceId);
      // ensure spaces/options are loaded
      await loadSpaces();
      // reveal panel
      // force-visible (use block to avoid CSS specificity issues)
      try {
        // Remove any static inline style attribute (e.g. "display:none") set in the markup
        // so runtime style changes are not masked by the original attribute.
        if (container.hasAttribute && container.hasAttribute('style')) container.removeAttribute('style');
        container.style.setProperty('display','block','important');
        container.style.setProperty('visibility','visible','important');
        container.style.setProperty('opacity','1','important');
        container.hidden = false;
        container.classList.remove('d-none');
        console.log('[booking] bookingContainer made visible');
        // dump computed styles and ancestor visibility to help debug layout issues
        const cs = window.getComputedStyle(container);
        console.log('[booking] bookingContainer computed style', { display: cs.display, visibility: cs.visibility, opacity: cs.opacity, offsetParent: container.offsetParent });
        let el = container;
        const ancestors = [];
        while(el && el !== document.documentElement){
          const s = window.getComputedStyle(el);
          ancestors.push({ tag: el.tagName, id: el.id || null, classes: el.className || null, display: s.display, visibility: s.visibility, opacity: s.opacity, offsetParent: el.offsetParent ? el.offsetParent.tagName : null });
          el = el.parentElement;
        }
        console.log('[booking] ancestor chain', ancestors);
      } catch (e) { console.warn('[booking] failed to force-show bookingContainer', e); }

      // help UX: bring booking panel into view and add a brief highlight so it's obvious
      try {
        container.scrollIntoView({ behavior: 'smooth', block: 'center' });
        container.style.setProperty('box-shadow', '0 12px 30px rgba(37,99,235,0.14)', 'important');
        container.style.setProperty('border', '1px solid rgba(37,99,235,0.18)', 'important');
        container.style.setProperty('z-index', '999', 'important');
        setTimeout(()=>{
          container.style.removeProperty('box-shadow');
          container.style.removeProperty('border');
          container.style.removeProperty('z-index');
        }, 3000);
      } catch(e){ /* ignore */ }
      // ensure the select contains the requested space
      let opt = Array.from(spaceSel.options).find(o => o.value == spaceId || o.value === String(Number(spaceId)));
      if (!opt) {
        opt = document.createElement('option');
        opt.value = String(spaceId);
        opt.textContent = `Space ${spaceId}`;
        spaceSel.appendChild(opt);
      }
      spaceSel.value = opt.value;
      // load bookings and render week
      await fetchBookedSlots();
      await renderWeek();
      console.log('[booking] showBookingForSpace() done');
    };

  })();
});
