import './app.css';
import { mount } from 'svelte';
import App from './App.svelte';
import { applyTheme, prefs } from './lib/prefs.svelte';

applyTheme(prefs.theme);
mount(App, { target: document.getElementById('app')! });
