import { Component, inject, signal, computed, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
interface Product { id:number; name:string; description:string; category:string; icon:string; priceCents:number }
interface CartLine { productId:number; quantity:number }
interface Quote { subtotalCents:number; deliveryCents:number; totalCents:number; currency:string }
interface OrderLine { productId:number; name:string; quantity:number; unitPriceCents:number; lineTotalCents:number }
interface Order extends Quote { id:string; createdAt:string; status:string; lines:OrderLine[] }
interface OrderStatus { name:string; isFinal:boolean; allowedTransitions:string[] }
interface Pending { key:string; items:CartLine[] }
@Component({selector:'app-root',standalone:true,imports:[CommonModule,FormsModule],templateUrl:'./shop.html'})
export class AppComponent implements OnInit {
 private http = inject(HttpClient);
 statuses=signal<OrderStatus[]>([]); selectedStatuses:Record<string,string>={};
 products=signal<Product[]>([]); cart=signal<CartLine[]>([]); orders=signal<Order[]>([]);
 quote=signal<Quote|null>(null); view=signal<'shop'|'orders'>('shop'); busy=signal(false); loading=signal(true);
 message=signal(''); error=signal(''); search=''; category='All'; pending=signal<Pending|null>(null);
 count=computed(()=>this.cart().reduce((sum,x)=>sum+x.quantity,0));
 categories=computed(()=>['All',...new Set(this.products().map(x=>x.category))]);
 async ngOnInit() {
  try {
   const saved=localStorage.getItem('little-shop-pending');
   if(saved) { const p:Pending=JSON.parse(saved); if(typeof p.key==='string' && Array.isArray(p.items)) {this.pending.set(p);this.cart.set(p.items);} }
   this.products.set(await firstValueFrom(this.http.get<Product[]>('/api/products')));
   if(this.cart().length) await this.refreshQuote();
  } catch(e) {this.fail(e);} finally {this.loading.set(false);}
 }
 filtered() {return this.products().filter(p=>(this.category==='All'||p.category===this.category)&&`${p.name} ${p.description}`.toLowerCase().includes(this.search.toLowerCase()));}
 product(id:number) {return this.products().find(p=>p.id===id);}
 money(cents:number) {return new Intl.NumberFormat('en-ZA',{style:'currency',currency:'ZAR'}).format(cents/100);}
 async add(p:Product) {
  const line=this.cart().find(x=>x.productId===p.id);
  await this.change(p.id,(line?.quantity??0)+1);
 }
 async change(id:number,quantity:number) {
  if(this.busy()||this.pending()) return;
  if(!Number.isInteger(quantity)||quantity<0||quantity>99) {this.error.set('Choose a whole quantity between 1 and 99, or remove the item.');return;}
  const items=this.cart().filter(x=>x.productId!==id);
  if(quantity)items.push({productId:id,quantity});
  this.cart.set(items);this.message.set('');await this.refreshQuote();
 }
 async refreshQuote() {
  this.error.set('');this.quote.set(null);
  if(!this.cart().length)return;
  this.busy.set(true);
  try {this.quote.set(await firstValueFrom(this.http.post<Quote>('/api/cart/quote',{items:this.cart()})));}
  catch(e){this.fail(e);}finally{this.busy.set(false);}
 }
 async checkout() {
  if(this.busy()||!this.cart().length)return;
  this.error.set('');this.message.set('');
  try {
   if(!this.pending()) {
    if(!this.quote())return;
    const p={key:crypto.randomUUID(),items:this.cart().map(x=>({...x}))};
    localStorage.setItem('little-shop-pending',JSON.stringify(p));this.pending.set(p);
   }
   this.busy.set(true);
   const p=this.pending()!;
   const order=await firstValueFrom(this.http.post<Order>('/api/orders',{items:p.items},{headers:{'Idempotency-Key':p.key}}));
   localStorage.removeItem('little-shop-pending');this.pending.set(null);this.cart.set([]);this.quote.set(null);
   this.message.set(`Order ${order.id.slice(0,8)} placed. Status: ${order.status}. Thank you for shopping with us!`);
   this.view.set('orders');await this.loadOrders();
  }catch(e){
   if(e instanceof HttpErrorResponse && e.status>=400 && e.status<500 && e.status!==408 && e.status!==429){
    localStorage.removeItem('little-shop-pending');this.pending.set(null);
   }
   this.fail(e);
  }finally{this.busy.set(false);}
 }
 async loadOrders() {
  this.busy.set(true);this.error.set('');
  try{
   const [orders,statuses]=await Promise.all([
    firstValueFrom(this.http.get<Order[]>('/api/orders')),
    firstValueFrom(this.http.get<OrderStatus[]>('/api/order-statuses'))
   ]);
   this.orders.set(orders);this.statuses.set(statuses);this.selectedStatuses={};
  }
  catch(e){this.fail(e);}finally{this.busy.set(false);}
 }
 availableStatuses(order:Order) {
  const status=this.statuses().find(x=>x.name===order.status);
  return status&&!status.isFinal?status.allowedTransitions:[];
 }
 canCancel(order:Order) {return this.availableStatuses(order).includes('Cancelled');}
 async updateStatus(order:Order) {
  const status=this.selectedStatuses[order.id];
  if(this.busy()||!status||!this.availableStatuses(order).includes(status))return;
  this.busy.set(true);this.error.set('');this.message.set('');
  try {
   const updated=await firstValueFrom(this.http.patch<Order>(`/api/orders/${order.id}/status`,{status,expectedStatus:order.status}));
   this.orders.update(orders=>orders.map(x=>x.id===updated.id?updated:x));
   delete this.selectedStatuses[order.id];
   this.message.set(`Order ${updated.id.slice(0,8)} status changed to ${updated.status}.`);
  }catch(e){this.fail(e);}finally{this.busy.set(false);}
 }
 async cancelOrder(order:Order) {
  if(this.busy()||!this.canCancel(order))return;
  this.busy.set(true);this.error.set('');this.message.set('');
  try {
   const updated=await firstValueFrom(this.http.post<Order>(`/api/orders/${order.id}/cancel`,{}));
   this.orders.update(orders=>orders.map(x=>x.id===updated.id?updated:x));
   this.message.set(`Order ${updated.id.slice(0,8)} cancelled. No payment was taken for this demo order.`);
  }catch(e){this.fail(e);}finally{this.busy.set(false);}
 }
 async showOrders() {this.view.set('orders');await this.loadOrders();}
 private fail(e:unknown) {
  if(e instanceof HttpErrorResponse) {
   const errors=e.error?.errors as Record<string,string[]>|undefined;
   this.error.set(errors?Object.values(errors).flat().join(' '):e.error?.title??'Unable to reach the shop. Check the API and try again.');
  }else this.error.set('Unable to save or load demo data. Check browser storage permissions and try again.');
 }
}
