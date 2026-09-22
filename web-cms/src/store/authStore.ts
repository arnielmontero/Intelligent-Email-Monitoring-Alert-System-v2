import { create } from "zustand";
import { persist } from "zustand/middleware";
import axios from "axios";

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? "http://localhost:8090/api/v1";

interface AuthUser {
  userId: string;
  email: string;
  displayName: string;
  roles: string[];
}

interface AuthState {
  accessToken: string | null;
  refreshToken: string | null;
  user: AuthUser | null;
  login: (email: string, password: string) => Promise<void>;
  refresh: () => Promise<string | null>;
  logout: () => Promise<void>;
  hasRole: (...roles: string[]) => boolean;
}

export const useAuthStore = create<AuthState>()(
  persist(
    (set, get) => ({
      accessToken: null,
      refreshToken: null,
      user: null,

      login: async (email, password) => {
        const response = await axios.post(`${API_BASE_URL}/auth/login`, { email, password });
        const data = response.data;
        set({
          accessToken: data.accessToken,
          refreshToken: data.refreshToken,
          user: {
            userId: data.userId,
            email: data.email,
            displayName: data.displayName,
            roles: data.roles,
          },
        });
      },

      refresh: async () => {
        const currentRefreshToken = get().refreshToken;
        if (!currentRefreshToken) return null;

        try {
          const response = await axios.post(`${API_BASE_URL}/auth/refresh`, {
            refreshToken: currentRefreshToken,
          });
          const data = response.data;
          set({
            accessToken: data.accessToken,
            refreshToken: data.refreshToken,
            user: {
              userId: data.userId,
              email: data.email,
              displayName: data.displayName,
              roles: data.roles,
            },
          });
          return data.accessToken as string;
        } catch {
          return null;
        }
      },

      logout: async () => {
        const currentRefreshToken = get().refreshToken;
        set({ accessToken: null, refreshToken: null, user: null });
        if (currentRefreshToken) {
          try {
            await axios.post(
              `${API_BASE_URL}/auth/logout`,
              { refreshToken: currentRefreshToken },
              { headers: { Authorization: `Bearer ${get().accessToken}` } }
            );
          } catch {
            // best-effort revoke; local session state is already cleared
          }
        }
      },

      hasRole: (...roles) => {
        const user = get().user;
        if (!user) return false;
        return roles.some((role) => user.roles.includes(role));
      },
    }),
    { name: "iemas-auth" }
  )
);
