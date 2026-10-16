const USERS = {
  Heilyn: '123Queso',
  Marcelina: '123Fortuna',
  Masteruser: 'Isaac.25$*'
};
const INVENTORY_KEY = 'ventasMovil_inventory';
const ORDERS_KEY = 'ventasMovil_orders';
const REQUESTS_KEY = 'ventasMovil_requests';

function readStorage(key) {
  try {
    const value = JSON.parse(localStorage.getItem(key) || '[]');
    return Array.isArray(value) ? value : [];
  } catch {
    return [];
  }
}

let inventory = readStorage(INVENTORY_KEY);
let orders = readStorage(ORDERS_KEY);
let requests = readStorage(REQUESTS_KEY);
let pendingPhoto = '';
let currentUser = '';

function canManageInventory() {
  return currentUser === 'Admin' || currentUser === 'Masteruser';
}

const loginScreen = document.getElementById('loginScreen');
const appScreen = document.getElementById('appScreen');
const loginForm = document.getElementById('loginForm');
const saleForm = document.getElementById('saleForm');
const productForm = document.getElementById('productForm');
const saleProduct = document.getElementById('saleProduct');
const paymentPlan = document.getElementById('paymentPlan');
const installments = document.getElementById('installments');

function persistData() {
  localStorage.setItem(INVENTORY_KEY, JSON.stringify(inventory));
  localStorage.setItem(ORDERS_KEY, JSON.stringify(orders));
  localStorage.setItem(REQUESTS_KEY, JSON.stringify(requests));
}

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, character => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  })[character]);
}

function formatMoney(value) {
  return `₡${Number(value || 0).toLocaleString('es-CR')}`;
}

function printReceipt(title, rows) {
  const printWindow = window.open('', '_blank', 'width=380,height=650');
  if (!printWindow) {
    alert('Permite las ventanas emergentes para imprimir el comprobante.');
    return;
  }
  const content = rows.map(([label, value]) => `<p><strong>${escapeHtml(label)}:</strong> ${escapeHtml(value)}</p>`).join('');
  printWindow.document.write(`<!DOCTYPE html><html lang="es"><head><meta charset="UTF-8"><title>${escapeHtml(title)}</title><style>
    @page { size: 58mm auto; margin: 3mm; }
    * { box-sizing: border-box; } body { width: 52mm; margin: 0 auto; color: #000; font: 12px Arial, sans-serif; }
    h1 { margin: 0 0 12px; font-size: 18px; text-align: center; } p { margin: 5px 0; overflow-wrap: anywhere; }
    .rule { border-top: 1px dashed #000; margin: 10px 0; }
    @media print { body { width: 52mm; } }
  </style></head><body><h1>${escapeHtml(title)}</h1><div class="rule"></div>${content}<div class="rule"></div><p style="text-align:center">Gracias</p><script>window.onload = () => { window.print(); window.onafterprint = () => window.close(); };<\/script></body></html>`);
  printWindow.document.close();
}

function invoiceRows(order) {
  const dates = order.installmentDates && order.installmentDates.length
    ? order.installmentDates.map((date, index) => `Cuota ${index + 1}: ${date}`).join(' | ')
    : 'Pago de contado';
  return [
    ['Factura', order.invoice], ['Fecha', new Date(order.createdAt).toLocaleString('es-CR')],
    ['Cliente', `${order.name} ${order.lastName}`], ['Cédula', order.customerId],
    ['Teléfono', order.phone], ['Dirección', order.address], ['Artículo', order.product],
    ['Precio', formatMoney(order.price)], ['Forma de pago', order.plan], ['Método de pago', order.method],
    ['Cuotas', dates], ['Vendedor', order.seller]
  ];
}

function requestRows(request) {
  return [
    ['Ticket', request.ticket], ['Nombre', request.name], ['Apellidos', request.lastName],
    ['Fecha del pedido', request.requestDate], ['Fecha de entrega', request.deliveryDate],
    ['Descripción', request.description],
    ['Precio aproximado', request.estimatedPrice === '' ? 'No indicado' : formatMoney(request.estimatedPrice)],
    ['Forma de pago', request.plan]
  ];
}

function nextInvoiceNumber() {
  const date = new Date();
  const datePart = `${date.getFullYear()}${String(date.getMonth() + 1).padStart(2, '0')}${String(date.getDate()).padStart(2, '0')}`;
  const sequence = String(orders.length + 1).padStart(4, '0');
  return `FAC-${datePart}-${sequence}`;
}

function refreshInvoice() {
  document.getElementById('invoiceNumber').value = nextInvoiceNumber();
}

function showView(viewId) {
  document.querySelectorAll('.view').forEach(view => view.classList.toggle('hidden', view.id !== viewId));
  document.querySelectorAll('.nav-item').forEach(button => button.classList.toggle('active', button.dataset.view === viewId));
  if (viewId === 'ventasView') refreshInvoice();
}

function renderProductSelect() {
  const available = inventory.filter(item => Number(item.stock) > 0);
  saleProduct.replaceChildren();
  const placeholder = document.createElement('option');
  placeholder.value = '';
  placeholder.textContent = available.length ? 'Selecciona un artículo' : 'Primero agrega artículos al inventario';
  saleProduct.append(placeholder);
  available.forEach(item => {
    const option = document.createElement('option');
    option.value = item.id;
    option.textContent = `${item.name} · ${item.stock} disponibles`;
    saleProduct.append(option);
  });
  saleProduct.disabled = available.length === 0;
  document.getElementById('productHint').textContent = available.length
    ? 'Selecciona un artículo disponible.'
    : 'Agrega artículos o unidades al inventario para registrar una venta.';
}

function renderInventory() {
  const list = document.getElementById('inventoryList');
  const canEdit = canManageInventory();
  document.querySelector('.inventory-actions').classList.toggle('hidden', !canEdit);
  productForm.classList.toggle('hidden', !canEdit);
  document.querySelector('#inventarioView .page-heading .eyebrow').textContent = canEdit ? 'PRODUCTOS' : 'PRODUCTOS · SOLO LECTURA';
  list.replaceChildren();
  document.getElementById('inventoryCount').textContent = inventory.length;
  if (!inventory.length) {
    list.innerHTML = '<div class="empty-state">Aún no hay artículos. Agrega tu primer producto arriba.</div>';
    return;
  }

  inventory.forEach(item => {
    const card = document.createElement('article');
    card.className = 'card product-card';
    if (typeof item.photo === 'string' && item.photo.startsWith('data:image/')) {
      const image = document.createElement('img');
      image.className = 'product-image';
      image.src = item.photo;
      image.alt = item.name;
      card.append(image);
    } else {
      const placeholder = document.createElement('div');
      placeholder.className = 'product-placeholder';
      placeholder.textContent = '▧';
      card.append(placeholder);
    }

    const info = document.createElement('div');
    info.className = 'product-info';
    const name = document.createElement('h3');
    name.textContent = item.name;
    const details = document.createElement('p');
    details.textContent = item.details || 'Sin detalles adicionales';
    const cost = document.createElement('p');
    cost.textContent = item.cost === undefined || item.cost === null || item.cost === ''
      ? 'Costo: No registrado'
      : `Costo: ${formatMoney(item.cost)}`;
    const stock = document.createElement('span');
    stock.className = 'stock-tag';
    stock.textContent = `${item.stock} unidades`;
    info.append(name, details, cost, stock);

    if (canEdit) {
      const remove = document.createElement('button');
      remove.className = 'delete-button';
      remove.type = 'button';
      remove.textContent = 'Eliminar';
      remove.setAttribute('aria-label', `Eliminar ${item.name} del inventario`);
      remove.addEventListener('click', () => {
        if (!canManageInventory()) return;
        inventory = inventory.filter(product => product.id !== item.id);
        persistData();
        renderAll();
      });
      card.append(info, remove);
    } else {
      card.append(info);
    }
    list.append(card);
  });
}

function renderOrders() {
  const list = document.getElementById('ordersList');
  list.replaceChildren();
  document.getElementById('orderCount').textContent = orders.length;
  if (!orders.length) {
    list.innerHTML = '<div class="empty-state">Todavía no hay ventas. Las facturas guardadas aparecerán aquí.</div>';
    return;
  }

  [...orders].reverse().forEach(order => {
    const card = document.createElement('article');
    card.className = 'card order-card';
    const top = document.createElement('div');
    top.className = 'order-top';
    const invoice = document.createElement('strong');
    invoice.textContent = order.invoice;
    const price = document.createElement('span');
    price.className = 'order-price';
    price.textContent = formatMoney(order.price);
    top.append(invoice, price);

    const info = document.createElement('div');
    info.className = 'order-info';
    const customer = document.createElement('h3');
    customer.textContent = `${order.name} ${order.lastName}`;
    const product = document.createElement('p');
    product.textContent = `${order.product} · ${order.plan} · ${order.method}`;
    const details = document.createElement('p');
    details.className = 'order-details';
    const dates = order.installmentDates && order.installmentDates.length
      ? `Fechas de cuotas: ${order.installmentDates.join(' · ')}`
      : 'Pago de contado';
    details.textContent = `Cédula: ${order.customerId} · Teléfono: ${order.phone} · ${dates}`;
    const address = document.createElement('p');
    address.textContent = `Dirección: ${order.address}`;
    const seller = document.createElement('p');
    seller.textContent = `Registrado por ${order.seller} · ${new Date(order.createdAt).toLocaleDateString('es-CR')}`;
    info.append(customer, product, details, address, seller);
    const actions = document.createElement('div');
    actions.className = 'receipt-actions';
    const viewButton = document.createElement('button');
    viewButton.className = 'button secondary';
    viewButton.type = 'button';
    viewButton.textContent = 'Ver factura';
    viewButton.addEventListener('click', () => showReceipt('Factura', invoiceRows(order)));
    const printButton = document.createElement('button');
    printButton.className = 'button secondary';
    printButton.type = 'button';
    printButton.textContent = 'Reimprimir';
    printButton.addEventListener('click', () => printReceipt('Factura', invoiceRows(order)));
    actions.append(viewButton, printButton);
    card.append(top, info, actions);
    list.append(card);
  });
}

function showReceipt(title, rows) {
  const details = rows.map(([label, value]) => `<p><strong>${escapeHtml(label)}:</strong> ${escapeHtml(value)}</p>`).join('');
  const receiptWindow = window.open('', '_blank', 'width=420,height=700');
  if (!receiptWindow) {
    alert('Permite las ventanas emergentes para ver la factura.');
    return;
  }
  receiptWindow.document.write(`<!DOCTYPE html><html lang="es"><head><meta charset="UTF-8"><title>${escapeHtml(title)}</title><style>body{max-width:320px;margin:24px auto;padding:18px;font:14px Arial,sans-serif;color:#172a27}h1{text-align:center;color:#176b57}p{padding:7px 0;border-bottom:1px solid #e5ece9;overflow-wrap:anywhere}button{width:100%;padding:12px;margin-top:12px;background:#176b57;color:white;border:0;border-radius:8px;font-size:14px}</style></head><body><h1>${escapeHtml(title)}</h1>${details}<button onclick="window.print()">Imprimir</button></body></html>`);
  receiptWindow.document.close();
}

function renderRequests() {
  const list = document.getElementById('requestsList');
  list.replaceChildren();
  document.getElementById('requestCount').textContent = requests.length;
  if (!requests.length) {
    list.innerHTML = '<div class="empty-state">Todavía no hay pedidos guardados.</div>';
    return;
  }
  [...requests].reverse().forEach(request => {
    const card = document.createElement('article');
    card.className = 'card order-card';
    const top = document.createElement('div');
    top.className = 'order-top';
    const ticket = document.createElement('strong');
    ticket.textContent = request.ticket;
    const price = document.createElement('span');
    price.className = 'order-price';
    price.textContent = request.estimatedPrice === '' ? 'Precio pendiente' : formatMoney(request.estimatedPrice);
    top.append(ticket, price);
    const info = document.createElement('div');
    info.className = 'order-info';
    const customer = document.createElement('h3');
    customer.textContent = `${request.name} ${request.lastName}`;
    const description = document.createElement('p');
    description.textContent = request.description;
    const dates = document.createElement('p');
    dates.className = 'order-details';
    dates.textContent = `Pedido: ${request.requestDate} · Entrega: ${request.deliveryDate} · ${request.plan}`;
    info.append(customer, description, dates);
    const actions = document.createElement('div');
    actions.className = 'receipt-actions';
    const viewButton = document.createElement('button');
    viewButton.className = 'button secondary';
    viewButton.type = 'button';
    viewButton.textContent = 'Ver ticket';
    viewButton.addEventListener('click', () => showReceipt('Pedido', requestRows(request)));
    const printButton = document.createElement('button');
    printButton.className = 'button secondary';
    printButton.type = 'button';
    printButton.textContent = 'Imprimir ticket';
    printButton.addEventListener('click', () => printReceipt('Pedido', requestRows(request)));
    actions.append(viewButton, printButton);
    card.append(top, info, actions);
    list.append(card);
  });
}

function renderAll() {
  renderProductSelect();
  renderInventory();
  renderOrders();
  renderRequests();
  refreshInvoice();
}

loginForm.addEventListener('submit', event => {
  event.preventDefault();
  const enteredUsername = document.getElementById('username').value.trim();
  const username = Object.keys(USERS).find(user => user.toLowerCase() === enteredUsername.toLowerCase());
  const password = document.getElementById('password').value;
  if (!username || USERS[username] !== password) {
    document.getElementById('loginError').textContent = 'Usuario o contraseña incorrectos.';
    return;
  }
  document.getElementById('loginError').textContent = '';
  currentUser = username;
  document.getElementById('welcomeUser').textContent = `Hola, ${username}`;
  loginScreen.classList.add('hidden');
  appScreen.classList.remove('hidden');
  renderAll();
});

document.getElementById('logoutButton').addEventListener('click', () => {
  currentUser = '';
  appScreen.classList.add('hidden');
  loginScreen.classList.remove('hidden');
  loginForm.reset();
});

document.querySelectorAll('.nav-item').forEach(button => {
  button.addEventListener('click', () => showView(button.dataset.view));
});

paymentPlan.addEventListener('change', () => {
  const isInstallmentPlan = paymentPlan.value === '3 cuotas';
  installments.classList.toggle('hidden', !isInstallmentPlan);
  installments.querySelectorAll('input[type="date"]').forEach(input => {
    input.required = isInstallmentPlan;
  });
});

saleForm.addEventListener('submit', event => {
  event.preventDefault();
  const product = inventory.find(item => item.id === saleProduct.value);
  if (!product || Number(product.stock) < 1) {
    document.getElementById('saleMessage').textContent = 'El artículo seleccionado ya no tiene unidades disponibles.';
    renderAll();
    return;
  }

  const price = Number(document.getElementById('salePrice').value);
  if (!Number.isFinite(price) || price < 0) return;
  const installmentDates = paymentPlan.value === '3 cuotas'
    ? ['date1', 'date2', 'date3'].map(id => document.getElementById(id).value)
    : [];

  const order = {
    invoice: document.getElementById('invoiceNumber').value,
    name: document.getElementById('customerName').value.trim(),
    lastName: document.getElementById('customerLastName').value.trim(),
    customerId: document.getElementById('customerId').value.trim(),
    phone: document.getElementById('customerPhone').value.trim(),
    address: document.getElementById('customerAddress').value.trim(),
    product: product.name,
    price,
    plan: paymentPlan.value,
    method: document.getElementById('paymentMethod').value,
    installmentDates,
    seller: document.getElementById('welcomeUser').textContent.replace('Hola, ', ''),
    createdAt: new Date().toISOString()
  };

  orders.push(order);
  product.stock = Number(product.stock) - 1;
  persistData();
  saleForm.reset();
  installments.classList.add('hidden');
  installments.querySelectorAll('input[type="date"]').forEach(input => { input.required = false; });
  document.getElementById('saleMessage').textContent = `Venta ${order.invoice} guardada correctamente.`;
  renderAll();
  printReceipt('Factura', invoiceRows(order));
});

function imageAsDataUrl(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const image = new Image();
      image.onload = () => {
        const scale = Math.min(1, 700 / Math.max(image.width, image.height));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(image.width * scale));
        canvas.height = Math.max(1, Math.round(image.height * scale));
        canvas.getContext('2d').drawImage(image, 0, 0, canvas.width, canvas.height);
        resolve(canvas.toDataURL('image/jpeg', 0.78));
      };
      image.onerror = reject;
      image.src = reader.result;
    };
    reader.onerror = reject;
    reader.readAsDataURL(file);
  });
}

document.getElementById('productPhoto').addEventListener('change', async event => {
  const file = event.target.files[0];
  pendingPhoto = '';
  const preview = document.getElementById('photoPreview');
  preview.replaceChildren();
  preview.classList.add('hidden');
  if (!file) return;
  try {
    pendingPhoto = await imageAsDataUrl(file);
    const image = document.createElement('img');
    image.src = pendingPhoto;
    image.alt = 'Vista previa del artículo';
    preview.append(image);
    preview.classList.remove('hidden');
  } catch {
    document.getElementById('inventoryMessage').textContent = 'No se pudo cargar la imagen.';
  }
});

document.getElementById('requestDate').value = new Date().toISOString().slice(0, 10);
document.getElementById('requestForm').addEventListener('submit', event => {
  event.preventDefault();
  const request = {
    ticket: `PED-${Date.now()}`,
    name: document.getElementById('requestName').value.trim(),
    lastName: document.getElementById('requestLastName').value.trim(),
    requestDate: document.getElementById('requestDate').value,
    deliveryDate: document.getElementById('deliveryDate').value,
    description: document.getElementById('requestDescription').value.trim(),
    estimatedPrice: document.getElementById('estimatedPrice').value,
    plan: document.getElementById('requestPlan').value
  };
  if (!request.name || !request.lastName || !request.requestDate || !request.deliveryDate || !request.description) return;
  requests.push(request);
  persistData();
  event.currentTarget.reset();
  document.getElementById('requestDate').value = new Date().toISOString().slice(0, 10);
  document.getElementById('requestMessage').textContent = `Ticket ${request.ticket} guardado correctamente.`;
  renderRequests();
});

productForm.addEventListener('submit', event => {
  event.preventDefault();
  if (!canManageInventory()) return;
  const name = document.getElementById('productName').value.trim();
  const cost = Number(document.getElementById('productCost').value);
  const stock = Number(document.getElementById('productStock').value);
  if (!name || !Number.isFinite(cost) || cost < 0 || !Number.isInteger(stock) || stock < 0) return;
  inventory.push({
    id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
    name,
    details: document.getElementById('productDetails').value.trim(),
    cost,
    stock,
    photo: pendingPhoto
  });
  persistData();
  productForm.reset();
  document.getElementById('productStock').value = '1';
  pendingPhoto = '';
  document.getElementById('photoPreview').replaceChildren();
  document.getElementById('photoPreview').classList.add('hidden');
  document.getElementById('inventoryMessage').textContent = 'Artículo agregado al inventario.';
  renderAll();
});

document.getElementById('exportButton').addEventListener('click', () => {
  if (!canManageInventory()) return;
  const file = new Blob([JSON.stringify({ products: inventory }, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(file);
  const link = document.createElement('a');
  link.href = url;
  link.download = 'inventario-ventas-movil.json';
  link.click();
  URL.revokeObjectURL(url);
});

document.getElementById('importButton').addEventListener('click', () => {
  if (canManageInventory()) document.getElementById('importFile').click();
});
document.getElementById('importFile').addEventListener('change', async event => {
  if (!canManageInventory()) {
    event.target.value = '';
    return;
  }
  const file = event.target.files[0];
  if (!file) return;
  try {
    const data = JSON.parse(await file.text());
    const products = Array.isArray(data) ? data : data.products;
    if (!Array.isArray(products) || products.some(item => !item || typeof item.name !== 'string')) {
      throw new Error('Formato inválido');
    }
    inventory = products.map(item => ({
      id: String(item.id || `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`),
      name: item.name.trim(),
      details: String(item.details || ''),
      cost: Number.isFinite(Number(item.cost)) && Number(item.cost) >= 0 ? Number(item.cost) : null,
      stock: Number.isInteger(Number(item.stock)) && Number(item.stock) >= 0 ? Number(item.stock) : 0,
      photo: typeof item.photo === 'string' && item.photo.startsWith('data:image/') ? item.photo : ''
    }));
    persistData();
    document.getElementById('inventoryMessage').textContent = `${inventory.length} artículo(s) importado(s).`;
    renderAll();
  } catch {
    document.getElementById('inventoryMessage').textContent = 'No se pudo importar. Selecciona un JSON exportado desde esta app.';
  }
  event.target.value = '';
});

renderAll();