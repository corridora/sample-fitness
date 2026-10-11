import { bootstrapApplication } from '@angular/platform-browser';
import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { context, trace, SpanStatusCode } from '@opentelemetry/api';

type Workout = { id: string; name: string; minutes: number; day: string };
type Job = { id: string; status: string; attempts: number; traceId: string; error?: string };
@Component({selector:'app-root', standalone:true, imports:[CommonModule], template:`
<header><a href="/" class="brand">● STRIDE<span>by Corridora</span></a><span class="pill">FICTIONAL FITNESS DATA</span></header>
<main><section class="intro"><p class="eyebrow">A LITTLE EVERY DAY</p><h1>Find your rhythm.</h1><p class="sub">Track your movement, build a weekly summary, and follow every request from browser to background job.</p><div class="stats"><div><strong>67</strong><span>minutes this week</span></div><div><strong>3</strong><span>active days</span></div><div><strong>2</strong><span>gentle recoveries</span></div></div></section>
<section class="content"><div><h2>Your movement <span class="count">3 workouts</span></h2><div class="workouts"><article *ngFor="let w of workouts()"><div class="icon">↗</div><div><h3>{{w.name}}</h3><span>{{w.day}} · {{w.minutes}} minutes</span></div><button [attr.data-testid]="'summary-'+w.id" (click)="summarize(w.id)">Summarize <span>→</span></button></article></div><div class="recommendation"><span>✦ RECOVERY INSIGHT</span><p>{{recommendation()}}</p><small>Deterministic fixture or your configured cross-cloud recommender.</small></div></div>
<aside><p class="eyebrow">WEEKLY SUMMARY</p><h2>Small steps.<br>Visible progress.</h2><p>Select a workout to create a queued weekly summary.</p><div class="job" *ngIf="job() as j"><span data-testid="job-status" [attr.data-status]="j.status">{{j.status}}</span><p>Attempts: {{j.attempts}} / 3</p><small *ngIf="j.error">{{j.error}}</small><code>{{j.traceId}}</code></div><div class="empty" *ngIf="!job()">Your next summary starts here.</div><p class="hint">The seeded tempo run fails during a short daily serializer regression. Recovery walk is always a healthy control.</p></aside></section>
<section class="diagnostic"><button data-testid="trace-check" (click)="checkTrace()">Check connected request</button><code data-testid="trace-id">{{traceId()}}</code><span>{{phase()}}</span></section><p class="error" role="alert" *ngIf="error()">{{error()}}</p></main><footer>Angular + ASP.NET Core · Azure Queue worker · OpenTelemetry</footer>`})
class App implements OnInit {
 workouts=signal<Workout[]>([]); job=signal<Job|null>(null); recommendation=signal('A gentle recovery day'); traceId=signal(''); phase=signal(''); error=signal('');
 async action<T>(name:string, request:()=>Promise<T>):Promise<T> {
   const span=trace.getTracer('sample-fitness-angular').startSpan(name);
   try { return await context.with(trace.setSpan(context.active(),span), request); }
   catch(e) { span.setStatus({code:SpanStatusCode.ERROR}); throw e; }
   finally { span.end(); }
 }
 async request<T>(url:string, init?:RequestInit):Promise<T> { const response=await fetch(url,init); if(!response.ok) throw new Error('Request failed: '+response.status); return response.json(); }
 async ngOnInit(){try{const data=await this.action('fitness.load',()=>this.request<{workouts:Workout[]}>('/api/workouts')); this.workouts.set(data.workouts); const r=await this.action('fitness.recommendations',()=>this.request<{suggestion?:string}>('/api/recommendations')); this.recommendation.set(r.suggestion??'Try a calm walk between harder sessions.'); const s=await this.request<{phase:string}>('/api/demo/status');this.phase.set(s.phase);}catch(e){this.error.set(String(e));}}
 async summarize(id:string){this.error.set('');try{let current=await this.action('fitness.weekly-summary',()=>this.request<Job>('/api/summaries',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({workout_id:id})}));this.job.set(current);const jobId=current.id;for(let i=0;i<60;i++){await new Promise(r=>setTimeout(r,1000));current=await this.action('fitness.summary-poll',()=>this.request<Job>('/api/summaries/'+jobId));this.job.set(current);if(['complete','poison'].includes(current.status))break;}}catch(e){this.error.set(String(e));}}
 async checkTrace(){try{const r=await this.action('fitness.trace-check',()=>this.request<{trace_id:string}>('/api/trace-check'));this.traceId.set(r.trace_id);}catch(e){this.error.set(String(e));}}
}
bootstrapApplication(App).catch(e=>console.error(e));
